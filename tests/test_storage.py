"""Tests du stockage SQLite."""

import unittest
from pathlib import Path
from tempfile import TemporaryDirectory

from tempi.sensor import Reading
from tempi.storage import Storage, choose_bucket


class StorageTests(unittest.TestCase):
    def setUp(self):
        self._tmp = TemporaryDirectory()
        self.path = Path(self._tmp.name) / "sous-dossier" / "tempi.db"
        self.storage = Storage(self.path)
        self.addCleanup(self._tmp.cleanup)
        self.addCleanup(self.storage.close)

    def test_creates_parent_directory(self):
        self.assertTrue(self.path.parent.is_dir())

    def test_records_and_reads_back(self):
        self.storage.record([Reading("28-aaaa", 21.5, 1000), Reading("28-bbbb", 4.25, 1000)])
        series = self.storage.series(0, 2000)
        self.assertEqual(series["28-aaaa"][0]["celsius"], 21.5)
        self.assertEqual(series["28-bbbb"][0]["celsius"], 4.25)

    def test_sensor_is_created_once(self):
        self.storage.record([Reading("28-aaaa", 20.0, 1000)])
        self.storage.record([Reading("28-aaaa", 20.5, 1060)])
        sensors = self.storage.sensors()
        self.assertEqual(len(sensors), 1)
        self.assertEqual(sensors[0]["count"], 2)

    def test_duplicate_timestamp_overwrites(self):
        self.storage.record([Reading("28-aaaa", 20.0, 1000)])
        self.storage.record([Reading("28-aaaa", 21.0, 1000)])
        series = self.storage.series(0, 2000)
        self.assertEqual(len(series["28-aaaa"]), 1)
        self.assertEqual(series["28-aaaa"][0]["celsius"], 21.0)

    def test_last_seen_tracks_newest(self):
        self.storage.record([Reading("28-aaaa", 20.0, 2000)])
        self.storage.record([Reading("28-aaaa", 20.0, 1000)])
        self.assertEqual(self.storage.sensors()[0]["last_seen"], 2000)

    def test_range_filter_is_inclusive(self):
        for ts in (100, 200, 300):
            self.storage.record([Reading("28-aaaa", 20.0, ts)])
        self.assertEqual(len(self.storage.series(200, 300)["28-aaaa"]), 2)
        self.assertEqual(self.storage.series(0, 50), {})

    def test_series_filters_by_address(self):
        self.storage.record([Reading("28-aaaa", 20.0, 100), Reading("28-bbbb", 10.0, 100)])
        series = self.storage.series(0, 200, ["28-bbbb"])
        self.assertEqual(list(series), ["28-bbbb"])

    def test_bucketing_aggregates(self):
        self.storage.record(
            [
                Reading("28-aaaa", 20.0, 0),
                Reading("28-aaaa", 22.0, 30),
                Reading("28-aaaa", 30.0, 60),
            ]
        )
        points = self.storage.series(0, 120, bucket=60)
        self.assertEqual(len(points["28-aaaa"]), 2)
        first = points["28-aaaa"][0]
        self.assertEqual(first["ts"], 0)
        self.assertEqual(first["celsius"], 21.0)
        self.assertEqual(first["min"], 20.0)
        self.assertEqual(first["max"], 22.0)
        self.assertEqual(first["samples"], 2)

    def test_raw_points_carry_single_sample(self):
        self.storage.record([Reading("28-aaaa", 20.0, 10)])
        point = self.storage.series(0, 100, bucket=0)["28-aaaa"][0]
        self.assertEqual(point["samples"], 1)
        self.assertEqual(point["min"], point["max"])

    def test_summary(self):
        for ts, celsius in ((10, 18.0), (20, 22.0), (30, 20.0)):
            self.storage.record([Reading("28-aaaa", celsius, ts)])
        summary = self.storage.summary(0, 100)["28-aaaa"]
        self.assertEqual(summary["min"], 18.0)
        self.assertEqual(summary["max"], 22.0)
        self.assertEqual(summary["avg"], 20.0)
        self.assertEqual(summary["samples"], 3)

    def test_latest_reports_most_recent(self):
        self.storage.record([Reading("28-aaaa", 20.0, 100), Reading("28-aaaa", 25.0, 200)])
        latest = self.storage.latest()
        self.assertEqual(latest[0]["celsius"], 25.0)
        self.assertEqual(latest[0]["ts"], 200)

    def test_latest_handles_sensor_without_readings(self):
        self.storage.sensor_id("28-aaaa")
        latest = self.storage.latest()
        self.assertEqual(len(latest), 1)
        self.assertIsNone(latest[0]["celsius"])

    def test_labels(self):
        self.storage.record([Reading("28-aaaa", 20.0, 100)])
        self.assertTrue(self.storage.set_label("28-aaaa", "Salon"))
        self.assertEqual(self.storage.sensors()[0]["label"], "Salon")
        self.assertFalse(self.storage.set_label("28-inconnu", "Cave"))

    def test_prune(self):
        for ts in (100, 200, 300):
            self.storage.record([Reading("28-aaaa", 20.0, ts)])
        self.assertEqual(self.storage.prune(250), 2)
        self.assertEqual(self.storage.stats()["readings"], 1)

    def test_time_range_on_empty_database(self):
        self.assertEqual(self.storage.time_range(), (None, None))

    def test_export_rows_are_ordered(self):
        self.storage.record([Reading("28-bbbb", 5.0, 200), Reading("28-aaaa", 20.0, 100)])
        rows = [(row["ts"], row["address"]) for row in self.storage.iter_rows(0, 1000)]
        self.assertEqual(rows, [(100, "28-aaaa"), (200, "28-bbbb")])

    def test_reopen_keeps_data(self):
        self.storage.record([Reading("28-aaaa", 20.0, 100)])
        self.storage.close()
        with Storage(self.path) as reopened:
            self.assertEqual(reopened.stats()["readings"], 1)


class BucketTests(unittest.TestCase):
    def test_short_span_keeps_fine_resolution(self):
        self.assertLessEqual(choose_bucket(3600, 800), 5)

    def test_long_span_is_downsampled(self):
        bucket = choose_bucket(30 * 86400, 800)
        self.assertGreaterEqual(bucket, 3600)
        self.assertLessEqual(30 * 86400 / bucket, 800 * 1.2)

    def test_degenerate_spans(self):
        self.assertEqual(choose_bucket(0), 0)
        self.assertEqual(choose_bucket(-5), 0)


if __name__ == "__main__":
    unittest.main()
