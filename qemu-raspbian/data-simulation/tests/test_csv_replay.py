from __future__ import annotations

import csv
from pathlib import Path
import tempfile
import unittest

from csv_replay import (
    CsvReplayError,
    EXPECTED_FIELDS,
    load_node_readings,
    reading_at,
    validate_range,
)


def sample_row(node_id: int, timestamp: str, temperature: str = "25.5") -> list[str]:
    return [
        str(node_id),
        timestamp,
        temperature,
        "60.5",
        "24.0",
        "55.0",
        "6.5",
        "1.2",
        "100.0",
        "0",
        "normal",
        "0",
    ]


class CsvReplayTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary_directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary_directory.cleanup)
        self.csv_path = Path(self.temporary_directory.name) / "readings.csv"

    def write_csv(self, header: list[str], rows: list[list[str]]) -> None:
        with self.csv_path.open("w", newline="", encoding="utf-8") as csv_file:
            writer = csv.writer(csv_file)
            writer.writerow(header)
            writer.writerows(rows)

    def test_load_node_readings_filters_in_order_and_converts_numeric_types(self) -> None:
        self.write_csv(
            EXPECTED_FIELDS,
            [
                sample_row(6, "2025-07-24T00:00:00Z"),
                sample_row(18, "2025-07-24T00:05:00Z"),
                sample_row(6, "2025-07-24T00:10:00Z", "26.75"),
            ],
        )

        readings = load_node_readings(self.csv_path, 6)

        self.assertEqual(2, len(readings))
        self.assertEqual(
            ["2025-07-24T00:00:00Z", "2025-07-24T00:10:00Z"],
            [reading["timestamp"] for reading in readings],
        )
        self.assertEqual(26.75, readings[1]["air_temperature"])
        self.assertIsInstance(readings[1]["air_temperature"], float)
        self.assertEqual(6, readings[1]["node_id"])
        self.assertIsInstance(readings[1]["node_id"], int)

    def test_load_node_readings_rejects_schema_mismatch(self) -> None:
        self.write_csv(EXPECTED_FIELDS[:-1], [sample_row(6, "timestamp")[:-1]])

        with self.assertRaisesRegex(CsvReplayError, "CSV schema mismatch"):
            load_node_readings(self.csv_path, 6)

    def test_load_node_readings_rejects_non_finite_number(self) -> None:
        self.write_csv(
            EXPECTED_FIELDS,
            [sample_row(6, "2025-07-24T00:00:00Z", "nan")],
        )

        with self.assertRaisesRegex(CsvReplayError, "air_temperature"):
            load_node_readings(self.csv_path, 6)

    def test_validate_range_accepts_inclusive_boundaries(self) -> None:
        readings = [{"position": 1}, {"position": 2}]

        validate_range(readings, 1, 2)

        self.assertEqual({"position": 1}, reading_at(readings, 1))
        self.assertEqual({"position": 2}, reading_at(readings, 2))

    def test_validate_range_rejects_invalid_boundaries(self) -> None:
        readings = [{"position": 1}, {"position": 2}]

        cases = [
            (0, 1, "start_position"),
            (2, 1, "end_position"),
            (1, 3, "stream length"),
        ]
        for start, end, expected_message in cases:
            with self.subTest(start=start, end=end):
                with self.assertRaisesRegex(CsvReplayError, expected_message):
                    validate_range(readings, start, end)

    def test_reading_at_returns_copy_and_rejects_zero(self) -> None:
        readings = [{"value": "original"}]

        selected = reading_at(readings, 1)
        selected["value"] = "changed"

        self.assertEqual("original", readings[0]["value"])
        with self.assertRaisesRegex(CsvReplayError, "outside"):
            reading_at(readings, 0)


if __name__ == "__main__":
    unittest.main()
