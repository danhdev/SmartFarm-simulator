from __future__ import annotations

import json
from pathlib import Path
import tempfile
import unittest

from mqtt_publisher import (
    MqttPublisher,
    MqttPublisherError,
    MqttSettings,
    availability_payload,
    full_jitter_delay,
    next_backoff_cap,
)
from replay_store import ReplayStore


class FakeMessageInfo:
    def __init__(self, published: bool = True, rc: int = 0) -> None:
        self._published = published
        self.rc = rc
        self.wait_timeout: float | None = None

    def wait_for_publish(self, timeout: float | None = None) -> None:
        self.wait_timeout = timeout

    def is_published(self) -> bool:
        return self._published


class FakeClient:
    def __init__(self, message_info: FakeMessageInfo | None = None) -> None:
        self.message_info = message_info or FakeMessageInfo()
        self.publications: list[dict[str, object]] = []
        self.will: dict[str, object] | None = None
        self.on_connect = None
        self.on_disconnect = None

    def will_set(self, topic: str, payload: str, qos: int, retain: bool) -> None:
        self.will = {
            "topic": topic,
            "payload": payload,
            "qos": qos,
            "retain": retain,
        }

    def publish(
        self, topic: str, payload: str, qos: int, retain: bool
    ) -> FakeMessageInfo:
        self.publications.append(
            {"topic": topic, "payload": payload, "qos": qos, "retain": retain}
        )
        return self.message_info


class MqttPublisherTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary_directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary_directory.cleanup)
        database_path = Path(self.temporary_directory.name) / "state.sqlite3"
        self.store = ReplayStore(database_path, 6, 1, 3)
        self.store.initialize()
        self.events: list[tuple[str, dict[str, object]]] = []
        self.settings = MqttSettings(
            host="10.0.2.2",
            port=1883,
            topic_prefix="mugvn/farm",
            qos=1,
            keepalive_seconds=60,
            reconnect_initial_seconds=1,
            reconnect_max_seconds=60,
        )

    def logger(self, event: str, **fields: object) -> None:
        self.events.append((event, fields))

    def publisher(self, client: FakeClient | None = None) -> MqttPublisher:
        selected_client = client or FakeClient()
        return MqttPublisher(
            self.settings,
            "pi4-1",
            self.store,
            self.logger,
            client_factory=lambda device_id: selected_client,
            random_source=lambda: 0.25,
        )

    def test_availability_payload_has_only_device_and_status(self) -> None:
        payload = json.loads(availability_payload("pi4-1", "offline"))

        self.assertEqual({"device_id": "pi4-1", "status": "offline"}, payload)
        with self.assertRaisesRegex(ValueError, "online.*offline"):
            availability_payload("pi4-1", "paused")

    def test_new_client_registers_retained_qos_one_offline_will(self) -> None:
        client = FakeClient()

        publisher = self.publisher(client)
        built_client = publisher._new_client()

        self.assertIs(client, built_client)
        self.assertEqual(
            {
                "topic": "mugvn/farm/pi4-1/avail",
                "payload": '{"device_id":"pi4-1","status":"offline"}',
                "qos": 1,
                "retain": True,
            },
            client.will,
        )

    def test_publish_oldest_uses_qos_one_and_deletes_only_after_puback(self) -> None:
        queued = self.store.enqueue(
            1,
            "uuid-1",
            {"message_id": "uuid-1", "timestamp": "source-time"},
        )
        client = FakeClient(FakeMessageInfo(published=True))

        delivered = self.publisher(client).publish_oldest_once(client)

        self.assertEqual(queued.sequence, delivered.sequence)
        self.assertEqual(
            {
                "topic": "mugvn/farm/pi4-1/env",
                "payload": queued.payload_json,
                "qos": 1,
                "retain": False,
            },
            client.publications[0],
        )
        self.assertEqual(0, self.store.pending_count())
        self.assertEqual("mqtt_delivery_succeeded", self.events[-1][0])

    def test_missing_puback_keeps_stable_message_pending_with_error(self) -> None:
        self.store.enqueue(1, "uuid-1", {"message_id": "uuid-1"})
        client = FakeClient(FakeMessageInfo(published=False))

        with self.assertRaisesRegex(MqttPublisherError, "PUBACK"):
            self.publisher(client).publish_oldest_once(client)

        pending = self.store.oldest_pending()
        self.assertIsNotNone(pending)
        assert pending is not None
        self.assertEqual("uuid-1", pending.message_id)
        self.assertEqual(1, pending.attempt_count)
        self.assertIn("PUBACK", pending.last_error)

    def test_publish_rejection_keeps_message_pending(self) -> None:
        self.store.enqueue(1, "uuid-1", {"message_id": "uuid-1"})
        client = FakeClient(FakeMessageInfo(published=False, rc=4))

        with self.assertRaisesRegex(MqttPublisherError, "result code 4"):
            self.publisher(client).publish_oldest_once(client)

        self.assertEqual(1, self.store.pending_count())
        self.assertEqual("mqtt_publish_failed", self.events[-1][0])

    def test_online_availability_is_qos_one_and_retained(self) -> None:
        client = FakeClient()

        self.publisher(client)._publish_availability(client, "online")

        self.assertEqual(
            {
                "topic": "mugvn/farm/pi4-1/avail",
                "payload": '{"device_id":"pi4-1","status":"online"}',
                "qos": 1,
                "retain": True,
            },
            client.publications[0],
        )

    def test_full_jitter_uses_entire_capped_range(self) -> None:
        self.assertEqual(0.0, full_jitter_delay(60.0, lambda: 0.0))
        self.assertEqual(15.0, full_jitter_delay(60.0, lambda: 0.25))
        self.assertAlmostEqual(60.0, full_jitter_delay(60.0, lambda: 1.0))

    def test_exponential_backoff_doubles_and_caps_at_sixty_seconds(self) -> None:
        caps = [1.0]
        for _ in range(7):
            caps.append(next_backoff_cap(caps[-1], 60.0))

        self.assertEqual([1.0, 2.0, 4.0, 8.0, 16.0, 32.0, 60.0, 60.0], caps)


if __name__ == "__main__":
    unittest.main()
