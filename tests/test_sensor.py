"""Tests de la lecture des capteurs 1-Wire."""

import unittest
from pathlib import Path
from tempfile import TemporaryDirectory

from tempi.sensor import (
    CrcError,
    OutOfRangeError,
    ResetValueError,
    SensorError,
    SimulatedBus,
    W1Bus,
    _parse_w1_slave,
)

GOOD = (
    "a1 01 4b 46 7f ff 0c 10 5c : crc=5c YES\n"
    "a1 01 4b 46 7f ff 0c 10 5c t=26062\n"
)
BAD_CRC = (
    "a1 01 4b 46 7f ff 0c 10 5c : crc=5c NO\n"
    "a1 01 4b 46 7f ff 0c 10 5c t=26062\n"
)
RESET = (
    "50 05 4b 46 7f ff 0c 10 1c : crc=1c YES\n"
    "50 05 4b 46 7f ff 0c 10 1c t=85000\n"
)
NEGATIVE = (
    "6a fe 4b 46 7f ff 0c 10 3f : crc=3f YES\n"
    "6a fe 4b 46 7f ff 0c 10 3f t=-5625\n"
)


class ParseTests(unittest.TestCase):
    def test_reads_temperature(self):
        self.assertAlmostEqual(_parse_w1_slave(GOOD), 26.062)

    def test_reads_negative_temperature(self):
        self.assertAlmostEqual(_parse_w1_slave(NEGATIVE), -5.625)

    def test_rejects_bad_crc(self):
        with self.assertRaises(CrcError):
            _parse_w1_slave(BAD_CRC)

    def test_rejects_truncated_frame(self):
        with self.assertRaises(SensorError):
            _parse_w1_slave("a1 01 : crc=5c YES\n")

    def test_rejects_missing_value(self):
        with self.assertRaises(SensorError):
            _parse_w1_slave("a1 : crc=5c YES\nrien ici\n")


class W1BusTests(unittest.TestCase):
    def setUp(self):
        self._tmp = TemporaryDirectory()
        self.root = Path(self._tmp.name)
        self.addCleanup(self._tmp.cleanup)

    def _device(self, address: str, payload: str) -> None:
        directory = self.root / address
        directory.mkdir(parents=True, exist_ok=True)
        (directory / "w1_slave").write_text(payload)

    def test_discovers_only_temperature_families(self):
        self._device("28-000005e2fdc3", GOOD)
        self._device("10-000801f2ab34", GOOD)
        self._device("w1_bus_master1", GOOD)
        bus = W1Bus(self.root)
        self.assertEqual(bus.discover(), ["10-000801f2ab34", "28-000005e2fdc3"])

    def test_missing_bus_directory_is_not_fatal(self):
        bus = W1Bus(self.root / "absent")
        self.assertFalse(bus.available())
        self.assertEqual(bus.discover(), [])

    def test_read_returns_celsius(self):
        self._device("28-000005e2fdc3", GOOD)
        bus = W1Bus(self.root)
        self.assertAlmostEqual(bus.read("28-000005e2fdc3"), 26.062)

    def test_reset_value_is_rejected(self):
        self._device("28-000005e2fdc3", RESET)
        bus = W1Bus(self.root, retries=1)
        with self.assertRaises(ResetValueError):
            bus.read("28-000005e2fdc3")

    def test_reset_value_can_be_allowed(self):
        self._device("28-000005e2fdc3", RESET)
        bus = W1Bus(self.root, retries=1, allow_reset_value=True)
        self.assertAlmostEqual(bus.read("28-000005e2fdc3"), 85.0)

    def test_out_of_range_is_rejected(self):
        self._device(
            "28-000005e2fdc3",
            "a1 : crc=5c YES\na1 t=200000\n",
        )
        bus = W1Bus(self.root, retries=1)
        with self.assertRaises(OutOfRangeError):
            bus.read("28-000005e2fdc3")

    def test_retries_before_failing(self):
        self._device("28-000005e2fdc3", BAD_CRC)
        bus = W1Bus(self.root, retries=3, retry_delay=0)
        calls = []
        original = bus._read_once

        def counting(address):
            calls.append(address)
            return original(address)

        bus._read_once = counting
        with self.assertRaises(CrcError):
            bus.read("28-000005e2fdc3")
        self.assertEqual(len(calls), 3)

    def test_read_all_isolates_failures(self):
        self._device("28-aaaa", GOOD)
        self._device("28-bbbb", BAD_CRC)
        bus = W1Bus(self.root, retries=1, retry_delay=0)
        readings, failures = bus.read_all()
        self.assertEqual([r.address for r in readings], ["28-aaaa"])
        self.assertEqual([address for address, _ in failures], ["28-bbbb"])

    def test_unknown_sensor_raises(self):
        bus = W1Bus(self.root, retries=1)
        with self.assertRaises(SensorError):
            bus.read("28-inconnu")


class SimulatedBusTests(unittest.TestCase):
    def test_produces_plausible_values(self):
        bus = SimulatedBus()
        readings, failures = bus.read_all()
        self.assertEqual(failures, [])
        self.assertEqual(len(readings), 2)
        for reading in readings:
            self.assertTrue(-10 < reading.celsius < 50, reading.celsius)


if __name__ == "__main__":
    unittest.main()
