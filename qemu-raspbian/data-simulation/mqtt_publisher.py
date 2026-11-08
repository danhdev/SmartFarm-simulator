#!/usr/bin/env python3
"""MQTT QoS 1 publisher that drains a persistent replay outbox."""

from __future__ import annotations

from dataclasses import dataclass
import json
import random
import threading
from typing import Any, Callable

from replay_store import OutboxMessage, ReplayStore


try:
    import paho.mqtt.client as paho_mqtt
except ImportError:  # The dependency is checked explicitly when a client is built.
    paho_mqtt = None


class MqttPublisherError(Exception):
    """Raised when the MQTT publisher cannot start or deliver a message."""


@dataclass(frozen=True)
class MqttSettings:
    host: str
    port: int
    topic_prefix: str
    qos: int
    keepalive_seconds: int
    reconnect_initial_seconds: float
    reconnect_max_seconds: float


def availability_payload(device_id: str, status: str) -> str:
    if status not in {"online", "offline"}:
        raise ValueError("availability status must be 'online' or 'offline'")
    return json.dumps(
        {"device_id": device_id, "status": status},
        separators=(",", ":"),
    )


def full_jitter_delay(backoff_cap: float, rng: Callable[[], float]) -> float:
    """Return full jitter in the inclusive conceptual range 0..backoff_cap."""
    if backoff_cap < 0:
        raise ValueError("backoff_cap must not be negative")
    return rng() * backoff_cap


def next_backoff_cap(current_cap: float, maximum_cap: float) -> float:
    if current_cap <= 0 or maximum_cap <= 0:
        raise ValueError("backoff caps must be greater than zero")
    return min(current_cap * 2, maximum_cap)


def _default_client_factory(device_id: str) -> Any:
    if paho_mqtt is None:
        raise MqttPublisherError(
            "paho-mqtt is not installed; install the python3-paho-mqtt package"
        )
    return paho_mqtt.Client(client_id=device_id, clean_session=True)


class MqttPublisher:
    """Reconnect to MQTT and deliver the SQLite outbox strictly oldest first."""

    ACK_TIMEOUT_SECONDS = 10.0

    def __init__(
        self,
        settings: MqttSettings,
        device_id: str,
        store: ReplayStore,
        log_event: Callable[..., None],
        client_factory: Callable[[str], Any] | None = None,
        random_source: Callable[[], float] | None = None,
    ) -> None:
        self.settings = settings
        self.device_id = device_id
        self.store = store
        self.log_event = log_event
        self.client_factory = client_factory or _default_client_factory
        self.random_source = random_source or random.random
        self.environment_topic = (
            f"{settings.topic_prefix.rstrip('/')}/{device_id}/env"
        )
        self.availability_topic = (
            f"{settings.topic_prefix.rstrip('/')}/{device_id}/avail"
        )
        self._connected = threading.Event()
        self._stop = threading.Event()
        self._wake = threading.Event()
        self._thread: threading.Thread | None = None
        self._client: Any | None = None
        self._clean_shutdown = False
        self._intentional_disconnect = False

    def _new_client(self) -> Any:
        client = self.client_factory(self.device_id)
        client.will_set(
            self.availability_topic,
            payload=availability_payload(self.device_id, "offline"),
            qos=self.settings.qos,
            retain=True,
        )
        client.on_connect = self._on_connect
        client.on_disconnect = self._on_disconnect
        return client

    def _on_connect(self, client: Any, userdata: Any, flags: Any, rc: Any, *args: Any) -> None:
        try:
            result_code = int(rc)
        except (TypeError, ValueError):
            result_code = int(getattr(rc, "value", -1))
        if result_code == 0:
            self._connected.set()
            self._wake.set()
            self.log_event("mqtt_connected", broker=self.settings.host)
        else:
            self.log_event("mqtt_connect_failed", result_code=result_code)

    def _on_disconnect(self, client: Any, userdata: Any, *args: Any) -> None:
        self._connected.clear()
        self._wake.set()
        if not self._intentional_disconnect:
            reason = str(args[0]) if args else "unknown"
            self.log_event("mqtt_disconnected", reason=reason)

    def start(self) -> None:
        if self._thread is not None:
            raise MqttPublisherError("MQTT publisher is already started")
        # Fail synchronously when paho-mqtt is missing or client construction fails.
        probe = self._new_client()
        self._client = probe
        self._thread = threading.Thread(
            target=self._run,
            name="mqtt-outbox-publisher",
            daemon=True,
        )
        self._thread.start()

    def notify_queue_changed(self) -> None:
        self._wake.set()

    def _wait_for_publish(self, message_info: Any) -> None:
        result_code = int(getattr(message_info, "rc", 0))
        if result_code != 0:
            raise MqttPublisherError(
                f"MQTT publish was rejected with result code {result_code}"
            )
        message_info.wait_for_publish(timeout=self.ACK_TIMEOUT_SECONDS)
        if not message_info.is_published():
            raise MqttPublisherError("timed out waiting for MQTT PUBACK")

    def _publish_availability(self, client: Any, status: str) -> None:
        message_info = client.publish(
            self.availability_topic,
            payload=availability_payload(self.device_id, status),
            qos=self.settings.qos,
            retain=True,
        )
        self._wait_for_publish(message_info)
        self.log_event("availability_published", status=status)

    def publish_oldest_once(self, client: Any) -> OutboxMessage | None:
        """Publish and acknowledge one FIFO row; leave failures pending."""
        message = self.store.oldest_pending()
        if message is None:
            return None
        self.store.record_attempt(message.sequence)
        try:
            message_info = client.publish(
                self.environment_topic,
                payload=message.payload_json,
                qos=self.settings.qos,
                retain=False,
            )
            self._wait_for_publish(message_info)
        except Exception as exc:
            self.store.record_error(message.sequence, str(exc))
            self.log_event(
                "mqtt_publish_failed",
                message_id=message.message_id,
                error=str(exc),
                queue_depth=self.store.pending_count(),
            )
            raise
        if not self.store.acknowledge(message.sequence, message.message_id):
            raise MqttPublisherError(
                f"PUBACKed outbox sequence {message.sequence} disappeared before delete"
            )
        self.log_event(
            "mqtt_delivery_succeeded",
            message_id=message.message_id,
            stream_position=message.stream_position,
            queue_depth=self.store.pending_count(),
        )
        return message

    def _disconnect(self, client: Any, publish_offline: bool) -> None:
        if publish_offline and self._connected.is_set():
            try:
                self._publish_availability(client, "offline")
            except Exception as exc:
                self.log_event("availability_publish_failed", error=str(exc))
        self._intentional_disconnect = True
        try:
            client.disconnect()
        except Exception:
            pass
        try:
            client.loop_stop()
        finally:
            self._intentional_disconnect = False
            self._connected.clear()

    def _run(self) -> None:
        backoff_cap = self.settings.reconnect_initial_seconds
        client = self._client
        while not self._stop.is_set():
            if client is None:
                try:
                    client = self._new_client()
                except Exception as exc:
                    self.log_event("mqtt_client_failed", error=str(exc))
                    return
            self._client = client
            self._connected.clear()
            try:
                client.connect(
                    self.settings.host,
                    self.settings.port,
                    keepalive=self.settings.keepalive_seconds,
                )
                client.loop_start()
                if not self._connected.wait(self.ACK_TIMEOUT_SECONDS):
                    raise MqttPublisherError("timed out waiting for MQTT CONNACK")
                self._publish_availability(client, "online")
                backoff_cap = self.settings.reconnect_initial_seconds

                while not self._stop.is_set() and self._connected.is_set():
                    if self.publish_oldest_once(client) is None:
                        self._wake.clear()
                        self._wake.wait(0.5)
            except Exception as exc:
                self.log_event("mqtt_connection_cycle_failed", error=str(exc))
            finally:
                self._disconnect(
                    client,
                    publish_offline=self._connected.is_set(),
                )
                client = None
                self._client = None

            if self._stop.is_set():
                break
            delay = full_jitter_delay(backoff_cap, self.random_source)
            self.log_event(
                "mqtt_reconnect_scheduled",
                delay_seconds=round(delay, 3),
                backoff_cap_seconds=backoff_cap,
            )
            self._stop.wait(delay)
            backoff_cap = next_backoff_cap(
                backoff_cap, self.settings.reconnect_max_seconds
            )

    def wait_until_empty(self, interrupted: threading.Event) -> bool:
        while self.store.pending_count() > 0:
            if interrupted.wait(0.1):
                return False
            self.notify_queue_changed()
        return True

    def stop(self, clean: bool = True) -> None:
        self._clean_shutdown = clean
        self._stop.set()
        self._wake.set()
        thread = self._thread
        if thread is not None:
            thread.join(self.ACK_TIMEOUT_SECONDS + 1.0)
            if thread.is_alive():
                self.log_event("mqtt_shutdown_timeout")
        self._thread = None
