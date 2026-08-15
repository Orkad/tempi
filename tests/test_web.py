"""Tests de l'API HTTP."""

import json
import threading
import time
import unittest
import urllib.error
import urllib.request
from pathlib import Path
from tempfile import TemporaryDirectory

from tempi.config import Config
from tempi.sensor import Reading
from tempi.storage import Storage
from tempi.web import BadRequest, make_server, parse_duration, parse_timestamp, resolve_window


def make_config(**overrides) -> Config:
    base = dict(
        db_path=Path(":memory:"),
        w1_dir=Path("/nonexistent"),
        simulate=True,
        read_retries=1,
        allow_reset_value=False,
        interval=60.0,
        min_delta=0.0,
        max_interval=0.0,
        retention_days=0,
        host="127.0.0.1",
        port=0,
    )
    base.update(overrides)
    return Config(**base)


class ParsingTests(unittest.TestCase):
    def test_durations(self):
        self.assertEqual(parse_duration("90s"), 90)
        self.assertEqual(parse_duration("30m"), 1800)
        self.assertEqual(parse_duration("24h"), 86400)
        self.assertEqual(parse_duration("7d"), 604800)
        self.assertEqual(parse_duration("2w"), 1209600)
        self.assertEqual(parse_duration(" 1.5h "), 5400)

    def test_invalid_duration(self):
        for value in ("", "abc", "10", "5y", "-3h"):
            with self.assertRaises(BadRequest, msg=value):
                parse_duration(value)

    def test_timestamps(self):
        self.assertEqual(parse_timestamp("1700000000"), 1700000000)
        self.assertEqual(parse_timestamp("2023-11-14T22:13:20Z"), 1700000000)
        self.assertEqual(parse_timestamp("2023-11-14T22:13:20+00:00"), 1700000000)

    def test_invalid_timestamp(self):
        with self.assertRaises(BadRequest):
            parse_timestamp("hier")


class WindowTests(unittest.TestCase):
    def setUp(self):
        self.storage = Storage(":memory:")
        self.addCleanup(self.storage.close)

    def test_defaults_to_last_day(self):
        start, end = resolve_window({}, self.storage)
        self.assertAlmostEqual(end - start, 86400, delta=2)

    def test_relative_range(self):
        start, end = resolve_window({"range": ["6h"]}, self.storage)
        self.assertAlmostEqual(end - start, 21600, delta=2)

    def test_explicit_bounds(self):
        self.assertEqual(resolve_window({"from": ["100"], "to": ["200"]}, self.storage), (100, 200))

    def test_range_all_uses_stored_extent(self):
        self.storage.record([Reading("28-aaaa", 20.0, 500), Reading("28-aaaa", 21.0, 900)])
        self.assertEqual(resolve_window({"range": ["all"]}, self.storage), (500, 900))

    def test_range_all_on_empty_database(self):
        start, end = resolve_window({"range": ["all"]}, self.storage)
        self.assertAlmostEqual(end - start, 86400, delta=2)

    def test_inverted_bounds_are_rejected(self):
        with self.assertRaises(BadRequest):
            resolve_window({"from": ["900"], "to": ["100"]}, self.storage)


class ApiTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls._tmp = TemporaryDirectory()
        cls.storage = Storage(Path(cls._tmp.name) / "tempi.db")

        now = int(time.time())
        cls.now = now
        cls.storage.record(
            [Reading("28-aaaa", 20.0 + i * 0.1, now - i * 60) for i in range(60)]
            + [Reading("28-bbbb", 5.0, now - i * 60) for i in range(60)]
        )
        cls.storage.set_label("28-aaaa", "Salon")

        config = make_config(port=0)
        cls.server = make_server(config, cls.storage)
        cls.base = f"http://127.0.0.1:{cls.server.server_address[1]}"
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        cls.thread.join(timeout=5)
        cls.storage.close()
        cls._tmp.cleanup()

    def get(self, path):
        with urllib.request.urlopen(self.base + path, timeout=10) as response:
            return response.status, response.read(), response.headers

    def get_json(self, path):
        status, body, _ = self.get(path)
        self.assertEqual(status, 200)
        return json.loads(body)

    def test_index_is_served(self):
        status, body, headers = self.get("/")
        self.assertEqual(status, 200)
        self.assertIn("text/html", headers["Content-Type"])
        self.assertIn(b"<title>tempi", body)

    def test_health(self):
        payload = self.get_json("/api/health")
        self.assertEqual(payload["status"], "ok")
        self.assertEqual(payload["storage"]["sensors"], 2)
        self.assertEqual(payload["storage"]["readings"], 120)

    def test_sensors(self):
        payload = self.get_json("/api/sensors")
        addresses = [s["address"] for s in payload["sensors"]]
        self.assertEqual(addresses, ["28-aaaa", "28-bbbb"])
        self.assertEqual(payload["sensors"][0]["label"], "Salon")

    def test_latest(self):
        payload = self.get_json("/api/latest")
        by_address = {s["address"]: s for s in payload["sensors"]}
        self.assertAlmostEqual(by_address["28-aaaa"]["celsius"], 20.0)
        self.assertEqual(by_address["28-aaaa"]["ts"], self.now)

    def test_series_returns_both_sensors(self):
        payload = self.get_json("/api/series?range=6h")
        self.assertEqual([s["address"] for s in payload["series"]], ["28-aaaa", "28-bbbb"])
        self.assertTrue(all(s["points"] for s in payload["series"]))
        self.assertEqual(payload["series"][0]["label"], "Salon")

    def test_series_filters_by_sensor(self):
        payload = self.get_json("/api/series?range=6h&sensor=28-bbbb")
        self.assertEqual([s["address"] for s in payload["series"]], ["28-bbbb"])

    def test_series_raw_bucket(self):
        payload = self.get_json("/api/series?range=6h&bucket=raw")
        self.assertEqual(payload["bucket"], 0)
        self.assertEqual(len(payload["series"][0]["points"]), 60)

    def test_series_explicit_bucket_aggregates(self):
        payload = self.get_json("/api/series?range=6h&bucket=3600")
        self.assertEqual(payload["bucket"], 3600)
        points = payload["series"][0]["points"]
        self.assertLess(len(points), 60)
        self.assertTrue(any(p["samples"] > 1 for p in points))

    def test_series_bucket_accepts_duration(self):
        payload = self.get_json("/api/series?range=6h&bucket=10m")
        self.assertEqual(payload["bucket"], 600)

    def test_summary(self):
        payload = self.get_json("/api/summary?range=6h")
        stats = payload["summary"]["28-bbbb"]
        self.assertEqual(stats["min"], 5.0)
        self.assertEqual(stats["max"], 5.0)
        self.assertEqual(stats["samples"], 60)

    def test_export_csv(self):
        status, body, headers = self.get("/api/export.csv?range=6h&sensor=28-bbbb")
        self.assertEqual(status, 200)
        self.assertIn("text/csv", headers["Content-Type"])
        lines = body.decode().strip().splitlines()
        self.assertEqual(lines[0], "timestamp_utc,epoch,address,label,celsius")
        self.assertEqual(len(lines), 61)

    def test_bad_request_returns_400(self):
        with self.assertRaises(urllib.error.HTTPError) as caught:
            self.get("/api/series?range=demain")
        self.assertEqual(caught.exception.code, 400)
        self.assertIn("error", json.loads(caught.exception.read()))

    def test_unknown_route_returns_404(self):
        with self.assertRaises(urllib.error.HTTPError) as caught:
            self.get("/api/inexistant")
        self.assertEqual(caught.exception.code, 404)

    def test_set_label(self):
        request = urllib.request.Request(
            self.base + "/api/sensors/28-bbbb/label",
            data=json.dumps({"label": "Congélateur"}).encode(),
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        with urllib.request.urlopen(request, timeout=10) as response:
            payload = json.loads(response.read())
        self.assertEqual(payload["label"], "Congélateur")
        self.addCleanup(self.storage.set_label, "28-bbbb", None)

    def test_set_label_on_unknown_sensor(self):
        request = urllib.request.Request(
            self.base + "/api/sensors/28-zzzz/label",
            data=b'{"label": "Nulle part"}',
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        with self.assertRaises(urllib.error.HTTPError) as caught:
            urllib.request.urlopen(request, timeout=10)
        self.assertEqual(caught.exception.code, 404)


if __name__ == "__main__":
    unittest.main()
