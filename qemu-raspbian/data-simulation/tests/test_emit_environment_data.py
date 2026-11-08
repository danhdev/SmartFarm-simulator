from __future__ import annotations

import argparse
from contextlib import redirect_stderr, redirect_stdout
import csv
import io
import json
from pathlib import Path
import tempfile
import threading
import unittest
from unittest.mock import patch
import uuid

import emit_environment_data
from csv_replay import EXPECTED_FIELDS
from replay_store import ReplayAlreadyRunningError, ReplayProcessLock, ReplayStore


class DrainingPublisher:
    def __init__(self, settings, device_id, store, log_event) -> None:
        self.store = store
        self.started = False
        self.stopped = False

    def start(self) -> None:
        self.started = True

    def notify_queue_changed(self) -> None:
        pass

    def wait_until_empty(self, interrupted: threading.Event) -> bool:
        while True:
            message = self.store.oldest_pending()
            if message is None:
                return True
            self.store.acknowledge(message.sequence, message.message_id)

    def stop(self, clean: bool = True) -> None:
        self.stopped = clean


def csv_row(node_id: int, timestamp: str) -> list[str]:
    return [
        str(node_id), timestamp, "25", "60", "24", "55", "6.5", "1.2",
        "100", "0", "normal", "0",
    ]


class EmitEnvironmentDataTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary_directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary_directory.cleanup)
        self.root = Path(self.temporary_directory.name)
        self.csv_path = self.root / "readings.csv"
        with self.csv_path.open("w", newline="", encoding="utf-8") as csv_file:
            writer = csv.writer(csv_file)
            writer.writerow(EXPECTED_FIELDS)
            writer.writerow(csv_row(6, "2025-07-24T00:00:00Z"))
            writer.writerow(csv_row(6, "2025-07-24T00:05:00Z"))
            writer.writerow(csv_row(6, "2025-07-24T00:10:00Z"))
        self.config_path = self.root / "config.json"
        self.config_path.write_text(
            json.dumps(
                {
                    "node_id": 6,
                    "csv_filename": "ignored.csv",
                    "state_db_filename": "ignored.sqlite3",
                    "interval_seconds": 300,
                    "start_position": 1,
                    "end_position": 3,
                    "reset": False,
                    "mqtt": {
                        "host": "10.0.2.2",
                        "port": 1883,
                        "topic_prefix": "mugvn/farm",
                        "qos": 1,
                        "keepalive_seconds": 60,
                        "reconnect_initial_seconds": 1,
                        "reconnect_max_seconds": 60,
                    },
                }
            ),
            encoding="utf-8",
        )

    def arguments(self, **overrides: object) -> argparse.Namespace:
        values = {
            "config": self.config_path,
            "csv": self.csv_path,
            "state_db": self.root / "state.sqlite3",
            "interval_seconds": 0.0,
            "limit": 2,
            "reset_state": False,
        }
        values.update(overrides)
        return argparse.Namespace(**values)

    def set_reset(self, value: object) -> None:
        raw = json.loads(self.config_path.read_text(encoding="utf-8"))
        raw["reset"] = value
        self.config_path.write_text(json.dumps(raw), encoding="utf-8")

    def test_build_payload_preserves_source_timestamp_and_stable_identifiers(self) -> None:
        payload = emit_environment_data.build_payload(
            "pi4-1",
            {"timestamp": "2025-07-24T00:00:00Z", "node_id": 6},
            message_id="uuid-1",
            emitted_at="2026-11-03T00:00:00.000Z",
        )

        self.assertEqual("uuid-1", payload["message_id"])
        self.assertEqual("pi4-1", payload["device_id"])
        self.assertEqual("2025-07-24T00:00:00Z", payload["timestamp"])
        self.assertEqual("2026-11-03T00:00:00.000Z", payload["emitted_at"])

    def test_build_payload_generates_distinct_uuid4_message_ids(self) -> None:
        first = emit_environment_data.build_payload("pi4-1", {"node_id": 6})
        second = emit_environment_data.build_payload("pi4-1", {"node_id": 6})

        first_uuid = uuid.UUID(first["message_id"])
        second_uuid = uuid.UUID(second["message_id"])
        self.assertEqual(4, first_uuid.version)
        self.assertEqual(4, second_uuid.version)
        self.assertNotEqual(first_uuid, second_uuid)

    def test_limited_run_advances_real_cursor_prints_json_and_exits_after_drain(self) -> None:
        stdout = io.StringIO()
        stderr = io.StringIO()
        with patch.object(emit_environment_data, "MqttPublisher", DrainingPublisher), patch.object(
            emit_environment_data.socket, "gethostname", return_value="pi4-1"
        ), redirect_stdout(stdout), redirect_stderr(stderr):
            result = emit_environment_data.run(self.arguments())

        lines = [json.loads(line) for line in stdout.getvalue().splitlines()]
        store = ReplayStore(self.root / "state.sqlite3", 6, 1, 3)
        store.initialize()
        events = [json.loads(line)["event"] for line in stderr.getvalue().splitlines()]
        self.assertEqual(0, result)
        self.assertEqual(2, len(lines))
        self.assertEqual(
            ["2025-07-24T00:00:00Z", "2025-07-24T00:05:00Z"],
            [line["timestamp"] for line in lines],
        )
        self.assertEqual(3, store.next_position())
        self.assertEqual(0, store.pending_count())
        self.assertIn("limited_batch_complete", events)

    def test_explicit_reset_reinitializes_cursor_without_starting_mqtt(self) -> None:
        self.set_reset(True)
        database_path = self.root / "state.sqlite3"
        store = ReplayStore(database_path, 6, 1, 3)
        store.initialize()
        store.enqueue(1, "uuid-1", {"message_id": "uuid-1"})
        stderr = io.StringIO()

        with patch.object(emit_environment_data, "MqttPublisher") as publisher, patch.object(
            emit_environment_data.socket, "gethostname", return_value="pi4-1"
        ), redirect_stderr(stderr):
            result = emit_environment_data.run(self.arguments(reset_state=True))

        reset_store = ReplayStore(database_path, 6, 1, 3)
        reset_store.initialize()
        self.assertEqual(0, result)
        self.assertEqual(1, reset_store.next_position())
        self.assertEqual(0, reset_store.pending_count())
        self.assertEqual("replay_state_reset", json.loads(stderr.getvalue())["event"])
        publisher.assert_not_called()

    def test_false_reset_resumes_cursor_and_preserves_pending_outbox(self) -> None:
        database_path = self.root / "state.sqlite3"
        store = ReplayStore(database_path, 6, 1, 3)
        store.initialize()
        store.enqueue(1, "saved-id", {"message_id": "saved-id"})
        observed_at_start = []

        class InspectingPublisher(DrainingPublisher):
            def start(self) -> None:
                observed_at_start.append(
                    (self.store.next_position(), self.store.oldest_pending().message_id)
                )
                super().start()

        stdout = io.StringIO()
        with patch.object(emit_environment_data, "MqttPublisher", InspectingPublisher), patch.object(
            emit_environment_data.socket, "gethostname", return_value="pi4-1"
        ), redirect_stdout(stdout):
            result = emit_environment_data.run(self.arguments(limit=1))

        self.assertEqual(0, result)
        self.assertEqual([(2, "saved-id")], observed_at_start)
        self.assertEqual("2025-07-24T00:05:00Z", json.loads(stdout.getvalue())["timestamp"])
        self.assertEqual(3, store.next_position())
        self.assertEqual(0, store.pending_count())

    def test_true_reset_clears_outbox_and_replays_from_configured_start(self) -> None:
        self.set_reset(True)
        database_path = self.root / "state.sqlite3"
        store = ReplayStore(database_path, 6, 1, 3)
        store.initialize()
        store.enqueue(1, "old-id", {"message_id": "old-id"})
        observed_at_start = []

        class InspectingPublisher(DrainingPublisher):
            def start(self) -> None:
                observed_at_start.append(
                    (self.store.next_position(), self.store.oldest_pending())
                )
                super().start()

        stdout = io.StringIO()
        stderr = io.StringIO()
        with patch.object(emit_environment_data, "MqttPublisher", InspectingPublisher), patch.object(
            emit_environment_data.socket, "gethostname", return_value="pi4-1"
        ), redirect_stdout(stdout), redirect_stderr(stderr):
            result = emit_environment_data.run(self.arguments(limit=3))

        messages = [json.loads(line) for line in stdout.getvalue().splitlines()]
        events = [json.loads(line)["event"] for line in stderr.getvalue().splitlines()]
        self.assertEqual(0, result)
        self.assertEqual([(1, None)], observed_at_start)
        self.assertEqual(
            ["2025-07-24T00:00:00Z", "2025-07-24T00:05:00Z", "2025-07-24T00:10:00Z"],
            [message["timestamp"] for message in messages],
        )
        self.assertNotIn("old-id", [message["message_id"] for message in messages])
        self.assertEqual(4, store.next_position())
        self.assertEqual(0, store.pending_count())
        self.assertIn("replay_state_reset", events)

    def test_true_reset_repeats_on_each_launch(self) -> None:
        self.set_reset(True)
        emitted = []
        with patch.object(emit_environment_data, "MqttPublisher", DrainingPublisher), patch.object(
            emit_environment_data.socket, "gethostname", return_value="pi4-1"
        ):
            for _ in range(2):
                stdout = io.StringIO()
                with redirect_stdout(stdout):
                    self.assertEqual(0, emit_environment_data.run(self.arguments(limit=1)))
                emitted.append(json.loads(stdout.getvalue()))

        store = ReplayStore(self.root / "state.sqlite3", 6, 1, 3)
        self.assertEqual(
            ["2025-07-24T00:00:00Z", "2025-07-24T00:00:00Z"],
            [message["timestamp"] for message in emitted],
        )
        self.assertNotEqual(emitted[0]["message_id"], emitted[1]["message_id"])
        self.assertEqual(2, store.next_position())

    def test_true_reset_refuses_while_replay_lock_is_held(self) -> None:
        self.set_reset(True)
        database_path = self.root / "state.sqlite3"
        store = ReplayStore(database_path, 6, 1, 3)
        store.initialize()
        store.enqueue(1, "saved-id", {"message_id": "saved-id"})

        with ReplayProcessLock(database_path), patch.object(
            emit_environment_data.socket, "gethostname", return_value="pi4-1"
        ), self.assertRaises(ReplayAlreadyRunningError):
            emit_environment_data.run(self.arguments())

        self.assertEqual(2, store.next_position())
        self.assertEqual("saved-id", store.oldest_pending().message_id)

    def test_unlimited_run_enters_idle_and_ctrl_c_preserves_queued_state(self) -> None:
        stdout = io.StringIO()
        stderr = io.StringIO()
        arguments = self.arguments(limit=None)
        with patch.object(emit_environment_data, "MqttPublisher", DrainingPublisher), patch.object(
            emit_environment_data.socket, "gethostname", return_value="pi4-1"
        ), patch.object(
            emit_environment_data.time, "sleep", side_effect=KeyboardInterrupt
        ), redirect_stdout(stdout), redirect_stderr(stderr):
            result = emit_environment_data.run(arguments)

        store = ReplayStore(self.root / "state.sqlite3", 6, 1, 3)
        store.initialize()
        events = [json.loads(line) for line in stderr.getvalue().splitlines()]
        source_complete = next(
            entry for entry in events if entry["event"] == "source_range_complete"
        )
        self.assertEqual(130, result)
        self.assertEqual("idle", source_complete["state"])
        self.assertEqual(4, store.next_position())
        self.assertEqual(3, store.pending_count())
        self.assertEqual("replay_interrupted", events[-1]["event"])

    def test_load_config_rejects_non_qos_one(self) -> None:
        raw = json.loads(self.config_path.read_text(encoding="utf-8"))
        raw["mqtt"]["qos"] = 0
        self.config_path.write_text(json.dumps(raw), encoding="utf-8")

        with self.assertRaisesRegex(emit_environment_data.ConfigurationError, "qos"):
            emit_environment_data.load_config(self.config_path)

    def test_load_config_requires_boolean_reset(self) -> None:
        for value in (None, 0, 1, "false", []):
            with self.subTest(value=value):
                self.set_reset(value)
                with self.assertRaisesRegex(
                    emit_environment_data.ConfigurationError, "reset.*boolean"
                ):
                    emit_environment_data.load_config(self.config_path)

        raw = json.loads(self.config_path.read_text(encoding="utf-8"))
        del raw["reset"]
        self.config_path.write_text(json.dumps(raw), encoding="utf-8")
        with self.assertRaisesRegex(
            emit_environment_data.ConfigurationError, "reset.*boolean"
        ):
            emit_environment_data.load_config(self.config_path)

    def test_all_host_configs_define_expected_nodes_and_complete_range(self) -> None:
        qemu_raspbian_directory = Path(__file__).resolve().parents[2]
        configuration_directory = qemu_raspbian_directory / "configuration"
        expected_nodes = {"pi4-1": 6, "pi4-2": 18, "pi4-3": 24, "pi4-4": 38}

        loaded = {
            name: emit_environment_data.load_config(
                configuration_directory / f"{name}.json"
            )
            for name in expected_nodes
        }

        self.assertEqual(expected_nodes, {name: item.node_id for name, item in loaded.items()})
        for name, item in loaded.items():
            with self.subTest(name=name):
                self.assertEqual((1, 288), (item.start_position, item.end_position))
                self.assertIs(item.reset, False)
                self.assertEqual("10.0.2.2", item.mqtt.host)
                self.assertEqual(1883, item.mqtt.port)
                self.assertEqual("mugvn/farm", item.mqtt.topic_prefix)
                self.assertEqual(1, item.mqtt.qos)


if __name__ == "__main__":
    unittest.main()
