#!/usr/bin/env python3
"""Load and validate one node's environmental replay stream."""

from __future__ import annotations

import csv
import math
from pathlib import Path
from typing import Any


EXPECTED_FIELDS = [
    "node_id",
    "timestamp",
    "air_temperature",
    "air_humidity",
    "soil_temperature",
    "soil_humidity",
    "ph",
    "ec",
    "light",
    "missing_flag",
    "anomaly_label",
    "violated_variable_count",
]
INTEGER_FIELDS = {"node_id", "missing_flag", "violated_variable_count"}
FLOAT_FIELDS = {
    "air_temperature",
    "air_humidity",
    "soil_temperature",
    "soil_humidity",
    "ph",
    "ec",
    "light",
}


class CsvReplayError(Exception):
    """Raised when replay CSV data or its selected range is invalid."""


def _convert_row(
    raw_row: dict[str, str | None], line_number: int
) -> dict[str, Any]:
    converted: dict[str, Any] = {}
    for field in EXPECTED_FIELDS:
        raw_value = raw_row.get(field)
        if raw_value is None or raw_value == "":
            raise CsvReplayError(f"line {line_number}: field '{field}' is empty")
        try:
            if field in INTEGER_FIELDS:
                converted[field] = int(raw_value)
            elif field in FLOAT_FIELDS:
                value = float(raw_value)
                if not math.isfinite(value):
                    raise ValueError("non-finite number")
                converted[field] = value
            else:
                converted[field] = raw_value
        except ValueError as exc:
            raise CsvReplayError(
                f"line {line_number}: invalid value for '{field}': {raw_value!r}"
            ) from exc
    return converted


def load_node_readings(csv_path: Path, node_id: int) -> list[dict[str, Any]]:
    """Return typed readings for one node in source-file order."""
    try:
        csv_file = csv_path.open(newline="", encoding="utf-8")
    except OSError as exc:
        raise CsvReplayError(f"cannot open CSV {csv_path}: {exc}") from exc

    with csv_file:
        reader = csv.DictReader(csv_file)
        if reader.fieldnames != EXPECTED_FIELDS:
            raise CsvReplayError(
                "CSV schema mismatch: expected "
                f"{EXPECTED_FIELDS!r}, found {reader.fieldnames!r}"
            )

        readings: list[dict[str, Any]] = []
        for line_number, raw_row in enumerate(reader, start=2):
            try:
                row_node_id = int(raw_row.get("node_id", ""))
            except (TypeError, ValueError) as exc:
                raise CsvReplayError(
                    f"line {line_number}: invalid value for 'node_id'"
                ) from exc
            if row_node_id == node_id:
                readings.append(_convert_row(raw_row, line_number))

    if not readings:
        raise CsvReplayError(f"CSV contains no readings for configured node_id {node_id}")
    return readings


def validate_range(
    readings: list[dict[str, Any]], start_position: int, end_position: int
) -> None:
    """Validate an inclusive, one-based range within a node-filtered stream."""
    if start_position <= 0:
        raise CsvReplayError("start_position must be greater than zero")
    if end_position < start_position:
        raise CsvReplayError("end_position must be at least start_position")
    if end_position > len(readings):
        raise CsvReplayError(
            f"end_position {end_position} exceeds the node stream length "
            f"of {len(readings)}"
        )


def reading_at(
    readings: list[dict[str, Any]], position: int
) -> dict[str, Any]:
    """Return a copy of the reading at a one-based stream position."""
    if position <= 0 or position > len(readings):
        raise CsvReplayError(
            f"stream position {position} is outside 1..{len(readings)}"
        )
    return dict(readings[position - 1])
