"""Tests de la boucle de collecte."""

import logging
import time
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory

from tempi.collector import Collector, apply_retention
from tempi.config import Config
from tempi.sensor import Reading, SensorError
from tempi.storage import Storage

# Plusieurs tests provoquent volontairement des erreurs de collecte : sans cela,
# leurs traces polluent la sortie de la suite.
logging.getLogger("tempi").setLevel(logging.CRITICAL)


def make_config(**overrides) -> Config:
    base = dict(
        db_path=Path(":memory:"),
        w1_dir=Path("/nonexistent"),
        simulate=True,
        read_retries=1,
        allow_reset_value=False,
        interval=0.01,
        min_delta=0.0,
        max_interval=0.0,
        retention_days=0,
        host="127.0.0.1",
        port=8080,
    )
    base.update(overrides)
    return Config(**base)


class FakeBus:
    """Bus scriptable : chaque appel consomme le lot suivant."""

    def __init__(self, batches):
        self.batches = list(batches)
        self.calls = 0

    def discover(self):
        return ["28-aaaa"]

    def read_all(self, addresses=None):
        self.calls += 1
        if not self.batches:
            return [], []
        return self.batches.pop(0)


class CollectorTests(unittest.TestCase):
    def setUp(self):
        self._tmp = TemporaryDirectory()
        self.storage = Storage(Path(self._tmp.name) / "tempi.db")
        self.addCleanup(self._tmp.cleanup)
        self.addCleanup(self.storage.close)

    def test_stores_each_reading(self):
        bus = FakeBus([([Reading("28-aaaa", 20.0, 100)], []), ([Reading("28-aaaa", 21.0, 160)], [])])
        collector = Collector(make_config(), self.storage, bus)
        collector.poll_once()
        collector.poll_once()
        self.assertEqual(collector.stored, 2)
        self.assertEqual(self.storage.stats()["readings"], 2)

    def test_failures_are_counted_not_raised(self):
        bus = FakeBus([([], [("28-aaaa", SensorError("CRC"))])])
        collector = Collector(make_config(), self.storage, bus)
        collector.poll_once()
        self.assertEqual(collector.errors, 1)
        self.assertEqual(collector.stored, 0)

    def test_min_delta_skips_stable_values(self):
        config = make_config(min_delta=0.5, max_interval=0)
        bus = FakeBus(
            [
                ([Reading("28-aaaa", 20.00, 100)], []),
                ([Reading("28-aaaa", 20.10, 160)], []),  # écart trop faible
                ([Reading("28-aaaa", 20.70, 220)], []),  # écart suffisant
            ]
        )
        collector = Collector(config, self.storage, bus)
        for _ in range(3):
            collector.poll_once()
        self.assertEqual(collector.stored, 2)
        points = self.storage.series(0, 1000)["28-aaaa"]
        self.assertEqual([p["ts"] for p in points], [100, 220])

    def test_max_interval_forces_a_point(self):
        config = make_config(min_delta=0.5, max_interval=100)
        bus = FakeBus(
            [
                ([Reading("28-aaaa", 20.0, 100)], []),
                ([Reading("28-aaaa", 20.0, 150)], []),  # trop tôt
                ([Reading("28-aaaa", 20.0, 200)], []),  # 100 s écoulées
            ]
        )
        collector = Collector(config, self.storage, bus)
        for _ in range(3):
            collector.poll_once()
        self.assertEqual([p["ts"] for p in self.storage.series(0, 1000)["28-aaaa"]], [100, 200])

    def test_min_delta_zero_stores_everything(self):
        config = make_config(min_delta=0.0)
        bus = FakeBus([([Reading("28-aaaa", 20.0, t)], []) for t in (100, 160, 220)])
        collector = Collector(config, self.storage, bus)
        for _ in range(3):
            collector.poll_once()
        self.assertEqual(collector.stored, 3)

    def test_run_stops_after_max_cycles(self):
        bus = FakeBus([([Reading("28-aaaa", 20.0, 100 + i)], []) for i in range(5)])
        collector = Collector(make_config(interval=0.001), self.storage, bus)
        started = time.monotonic()
        collector.run(max_cycles=3)
        self.assertEqual(collector.cycles, 3)
        self.assertLess(time.monotonic() - started, 5)

    def test_run_survives_a_failing_cycle(self):
        class ExplodingBus(FakeBus):
            def read_all(self, addresses=None):
                self.calls += 1
                if self.calls == 1:
                    raise RuntimeError("bus indisponible")
                return [Reading("28-aaaa", 20.0, 100)], []

        collector = Collector(make_config(interval=0.001), self.storage, ExplodingBus([]))
        collector.run(max_cycles=2)
        self.assertEqual(collector.errors, 1)
        self.assertEqual(collector.stored, 1)

    def test_stop_ends_the_loop(self):
        bus = FakeBus([([Reading("28-aaaa", 20.0, 100)], [])])
        collector = Collector(make_config(interval=30), self.storage, bus)
        collector.stop()
        collector.run()
        self.assertEqual(collector.cycles, 0)


class RetentionTests(unittest.TestCase):
    def setUp(self):
        self._tmp = TemporaryDirectory()
        self.storage = Storage(Path(self._tmp.name) / "tempi.db")
        self.addCleanup(self._tmp.cleanup)
        self.addCleanup(self.storage.close)

    def test_removes_old_readings_only(self):
        now = int(time.time())
        self.storage.record(
            [
                Reading("28-aaaa", 20.0, now - 10 * 86400),
                Reading("28-aaaa", 21.0, now - 1 * 86400),
            ]
        )
        removed = apply_retention(make_config(retention_days=7), self.storage)
        self.assertEqual(removed, 1)
        self.assertEqual(self.storage.stats()["readings"], 1)

    def test_disabled_retention_is_a_noop(self):
        self.storage.record([Reading("28-aaaa", 20.0, 1)])
        self.assertEqual(apply_retention(make_config(retention_days=0), self.storage), 0)
        self.assertEqual(self.storage.stats()["readings"], 1)


if __name__ == "__main__":
    unittest.main()
