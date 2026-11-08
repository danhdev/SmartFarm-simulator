#!/usr/bin/env python3
"""Queue one node's CSV readings and publish them to MQTT with recovery."""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import json
import math
from pathlib import Path
import socket
import sys
import threading
import time
from typing import Any, Callable
import uuid

from csv_replay import CsvReplayError, load_node_readings, reading_at, validate_range
from mqtt_publisher import MqttPublisher, MqttPublisherError, MqttSettings
from replay_store import (
    ReplayAlreadyRunningError,
    ReplayProcessLock,
    ReplayStateMismatchError,
    ReplayStore,
    ReplayStoreError,
    utc_now,
)


SCRIPT_DIR = Path(__file__).resolve().parent
DEFAULT_CONFIG = SCRIPT_DIR / "replay_config.json"


class ConfigurationError(Exception):
    """Raised when replay configuration is missing or invalid."""


@dataclass(frozen=True)
class ReplayConfig:
    node_id: int
    csv_filename: str
    state_db_filename: str
    interval_seconds: float
    start_position: int
    end_position: int
    reset: bool
    mqtt: MqttSettings


def positive_int(value: str) -> int:
    try:
        parsed = int(value)
    except ValueError as exc:
        raise argparse.ArgumentTypeError("must be an integer") from exc
    if parsed <= 0:
        raise argparse.ArgumentTypeError("must be greater than zero")
    return parsed


def nonnegative_float(value: str) -> float:
    try:
        parsed = float(value)
    except ValueError as exc:
        raise argparse.ArgumentTypeError("must be a number") from exc
    if not math.isfinite(parsed) or parsed < 0:
        raise argparse.ArgumentTypeError("must be a finite number at least zero")
    return parsed


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Persist and publish one configured node's CSV readings."
    )
    parser.add_argument(
        "--config",
        type=Path,
        default=DEFAULT_CONFIG,
        help=f"replay JSON configuration (default: {DEFAULT_CONFIG})",
    )
    parser.add_argument("--csv", type=Path, help="override the configured CSV path")
    parser.add_argument(
        "--state-db", type=Path, help="override the configured SQLite path"
    )
    parser.add_argument(
        "--interval-seconds",
        type=nonnegative_float,
        help="override the configured delay between newly queued readings",
    )
    parser.add_argument(
        "--limit",
        type=positive_int,
        help=(
            "queue at most the next N persisted positions, drain them, and exit; "
            "this advances the real replay cursor"
        ),
    )
    parser.add_argument(
        "--reset-state",
        action="store_true",
        help="replace SQLite state with a clean cursor and exit",
    )
    return parser.parse_args()


def _required_object(container: dict[str, Any], field: str) -> dict[str, Any]:
    value = container.get(field)
    if not isinstance(value, dict):
        raise ConfigurationError(f"configuration field '{field}' must be an object")
    return value


def _required_string(container: dict[str, Any], field: str) -> str:
    value = container.get(field)
    if not isinstance(value, str) or not value.strip():
        raise ConfigurationError(
            f"configuration field '{field}' must be a non-empty string"
        )
    return value


def _required_int(container: dict[str, Any], field: str, minimum: int = 1) -> int:
    value = container.get(field)
    if isinstance(value, bool) or not isinstance(value, int) or value < minimum:
        raise ConfigurationError(
            f"configuration field '{field}' must be an integer at least {minimum}"
        )
    return value


def _required_bool(container: dict[str, Any], field: str) -> bool:
    value = container.get(field)
    if not isinstance(value, bool):
        raise ConfigurationError(f"configuration field '{field}' must be a boolean")
    return value


def _required_number(
    container: dict[str, Any], field: str, minimum: float = 0.0
) -> float:
    value = container.get(field)
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ConfigurationError(
            f"configuration field '{field}' must be a number"
        )
    parsed = float(value)
    if not math.isfinite(parsed) or parsed < minimum:
        raise ConfigurationError(
            f"configuration field '{field}' must be finite and at least {minimum}"
        )
    return parsed


def load_config(config_path: Path) -> ReplayConfig:
    try:
        with config_path.open(encoding="utf-8") as config_file:
            raw = json.load(config_file)
    except FileNotFoundError as exc:
        raise ConfigurationError(f"configuration file not found: {config_path}") from exc
    except (OSError, json.JSONDecodeError) as exc:
        raise ConfigurationError(
            f"cannot read configuration {config_path}: {exc}"
        ) from exc
    if not isinstance(raw, dict):
        raise ConfigurationError("configuration must be a JSON object")

    start_position = _required_int(raw, "start_position")
    end_position = _required_int(raw, "end_position")
    if end_position < start_position:
        raise ConfigurationError("end_position must be at least start_position")
    mqtt_raw = _required_object(raw, "mqtt")
    mqtt = MqttSettings(
        host=_required_string(mqtt_raw, "host"),
        port=_required_int(mqtt_raw, "port"),
        topic_prefix=_required_string(mqtt_raw, "topic_prefix").rstrip("/"),
        qos=_required_int(mqtt_raw, "qos"),
        keepalive_seconds=_required_int(mqtt_raw, "keepalive_seconds"),
        reconnect_initial_seconds=_required_number(
            mqtt_raw, "reconnect_initial_seconds", minimum=0.001
        ),
        reconnect_max_seconds=_required_number(
            mqtt_raw, "reconnect_max_seconds", minimum=0.001
        ),
    )
    if mqtt.qos != 1:
        raise ConfigurationError("mqtt.qos must be 1 for the persistent outbox")
    if mqtt.reconnect_max_seconds < mqtt.reconnect_initial_seconds:
        raise ConfigurationError(
            "mqtt.reconnect_max_seconds must be at least reconnect_initial_seconds"
        )
    if "+" in mqtt.topic_prefix or "#" in mqtt.topic_prefix:
        raise ConfigurationError("mqtt.topic_prefix must not contain MQTT wildcards")

    return ReplayConfig(
        node_id=_required_int(raw, "node_id"),
        csv_filename=_required_string(raw, "csv_filename"),
        state_db_filename=_required_string(raw, "state_db_filename"),
        interval_seconds=_required_number(raw, "interval_seconds"),
        start_position=start_position,
        end_position=end_position,
        reset=_required_bool(raw, "reset"),
        mqtt=mqtt,
    )


def resolve_runtime_path(path_value: str | Path, override: Path | None = None) -> Path:
    selected = override if override is not None else Path(path_value)
    if selected.is_absolute():
        return selected
    return SCRIPT_DIR / selected


def make_logger(device_id: str) -> Callable[..., None]:
    def log_event(event: str, **fields: Any) -> None:
        entry = {
            "logged_at": utc_now(),
            "event": event,
            "device_id": device_id,
            **fields,
        }
        print(
            json.dumps(entry, separators=(",", ":"), allow_nan=False),
            file=sys.stderr,
            flush=True,
        )

    return log_event


def build_payload(
    device_id: str,
    reading: dict[str, Any],
    message_id: str | None = None,
    emitted_at: str | None = None,
) -> dict[str, Any]:
    return {
        "message_id": message_id or str(uuid.uuid4()),
        "device_id": device_id,
        "emitted_at": emitted_at or utc_now(),
        **reading,
    }


def run(args: argparse.Namespace) -> int:
    config = load_config(args.config)
    csv_path = resolve_runtime_path(config.csv_filename, args.csv)
    database_path = resolve_runtime_path(config.state_db_filename, args.state_db)
    interval_seconds = (
        config.interval_seconds
        if args.interval_seconds is None
        else args.interval_seconds
    )
    readings = load_node_readings(csv_path, config.node_id)
    validate_range(readings, config.start_position, config.end_position)

    device_id = socket.gethostname()
    if not device_id:
        raise ConfigurationError("could not determine a non-empty device hostname")
    log_event = make_logger(device_id)

    with ReplayProcessLock(database_path):
        if args.reset_state or config.reset:
            ReplayStore.reset_database(
                database_path,
                config.node_id,
                config.start_position,
                config.end_position,
            )
            log_event(
                "replay_state_reset",
                state_db=str(database_path),
                next_position=config.start_position,
            )
            if args.reset_state:
                return 0

        store = ReplayStore(
            database_path,
            config.node_id,
            config.start_position,
            config.end_position,
        )
        store.initialize()
        publisher = MqttPublisher(config.mqtt, device_id, store, log_event)
        publisher.start()
        interrupted = threading.Event()
        queued_this_run = 0
        try:
            while not store.source_complete():
                if args.limit is not None and queued_this_run >= args.limit:
                    break
                position = store.next_position()
                payload = build_payload(device_id, reading_at(readings, position))
                message = store.enqueue(
                    position, str(payload["message_id"]), payload
                )
                print(message.payload_json, flush=True)
                queued_this_run += 1
                log_event(
                    "reading_queued",
                    message_id=message.message_id,
                    stream_position=position,
                    queue_depth=store.pending_count(),
                )
                publisher.notify_queue_changed()
                if not store.source_complete() and (
                    args.limit is None or queued_this_run < args.limit
                ):
                    interrupted.wait(interval_seconds)

            if args.limit is not None:
                publisher.wait_until_empty(interrupted)
                log_event(
                    "limited_batch_complete",
                    queued_count=queued_this_run,
                    next_position=store.next_position(),
                )
                return 0

            log_event(
                "source_range_complete",
                next_position=store.next_position(),
                queue_depth=store.pending_count(),
                state="idle",
            )
            while True:
                time.sleep(1.0)
        except KeyboardInterrupt:
            interrupted.set()
            log_event(
                "replay_interrupted",
                next_position=store.next_position(),
                queue_depth=store.pending_count(),
            )
            return 130
        finally:
            interrupted.set()
            publisher.stop(clean=True)


def main() -> int:
    args = parse_args()
    try:
        return run(args)
    except (
        ConfigurationError,
        CsvReplayError,
        MqttPublisherError,
        ReplayAlreadyRunningError,
        ReplayStateMismatchError,
        ReplayStoreError,
    ) as exc:
        print(
            json.dumps(
                {
                    "logged_at": utc_now(),
                    "event": "replay_error",
                    "error": str(exc),
                },
                separators=(",", ":"),
            ),
            file=sys.stderr,
            flush=True,
        )
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
