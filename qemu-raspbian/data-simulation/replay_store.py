#!/usr/bin/env python3
"""Persistent SQLite cursor and MQTT outbox for environmental replay."""

from __future__ import annotations

from contextlib import contextmanager
from dataclasses import dataclass
from datetime import datetime, timezone
import fcntl
import json
import os
from pathlib import Path
import sqlite3
import tempfile
from typing import Any


class ReplayStoreError(Exception):
    """Raised when persistent replay state cannot be used safely."""


class ReplayAlreadyRunningError(ReplayStoreError):
    """Raised when another replay process owns the state lock."""


class ReplayStateMismatchError(ReplayStoreError):
    """Raised when configuration no longer matches persisted replay state."""


@dataclass(frozen=True)
class OutboxMessage:
    sequence: int
    stream_position: int
    message_id: str
    payload_json: str
    attempt_count: int
    last_error: str | None


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace(
        "+00:00", "Z"
    )


class ReplayProcessLock:
    """Advisory per-database lock shared by replay and explicit reset."""

    def __init__(self, database_path: Path) -> None:
        self.path = database_path.with_name(database_path.name + ".lock")
        self._file: Any | None = None

    def __enter__(self) -> "ReplayProcessLock":
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self._file = self.path.open("a+", encoding="utf-8")
        try:
            fcntl.flock(self._file.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as exc:
            self._file.close()
            self._file = None
            raise ReplayAlreadyRunningError(
                "the replay application is already running; stop it before resetting "
                "or starting another instance"
            ) from exc
        self._file.seek(0)
        self._file.truncate()
        self._file.write(str(os.getpid()))
        self._file.flush()
        return self

    def __exit__(self, exc_type: Any, exc: Any, traceback: Any) -> None:
        if self._file is not None:
            fcntl.flock(self._file.fileno(), fcntl.LOCK_UN)
            self._file.close()
            self._file = None


class ReplayStore:
    """SQLite-backed replay metadata and FIFO outbox."""

    def __init__(
        self,
        database_path: Path,
        node_id: int,
        start_position: int,
        end_position: int,
    ) -> None:
        self.database_path = database_path
        self.node_id = node_id
        self.start_position = start_position
        self.end_position = end_position

    @contextmanager
    def _connect(self, path: Path | None = None) -> Any:
        selected_path = path or self.database_path
        connection = sqlite3.connect(selected_path, timeout=30.0)
        connection.row_factory = sqlite3.Row
        connection.execute("PRAGMA journal_mode = DELETE")
        connection.execute("PRAGMA foreign_keys = ON")
        try:
            with connection:
                yield connection
        finally:
            connection.close()

    @staticmethod
    def _create_schema(connection: sqlite3.Connection) -> None:
        connection.executescript(
            """
            CREATE TABLE IF NOT EXISTS replay_state (
                singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
                node_id INTEGER NOT NULL,
                start_position INTEGER NOT NULL,
                end_position INTEGER NOT NULL,
                next_position INTEGER NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS outbox (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                stream_position INTEGER NOT NULL UNIQUE,
                message_id TEXT NOT NULL UNIQUE,
                payload_json TEXT NOT NULL,
                queued_at TEXT NOT NULL,
                attempt_count INTEGER NOT NULL DEFAULT 0,
                last_error TEXT
            );
            """
        )

    @staticmethod
    def _insert_initial_state(
        connection: sqlite3.Connection,
        node_id: int,
        start_position: int,
        end_position: int,
    ) -> None:
        connection.execute(
            """
            INSERT INTO replay_state (
                singleton_id, node_id, start_position, end_position,
                next_position, created_at
            ) VALUES (1, ?, ?, ?, ?, ?)
            """,
            (node_id, start_position, end_position, start_position, utc_now()),
        )

    def initialize(self) -> None:
        self.database_path.parent.mkdir(parents=True, exist_ok=True)
        with self._connect() as connection:
            self._create_schema(connection)
            state = connection.execute(
                "SELECT node_id, start_position, end_position FROM replay_state "
                "WHERE singleton_id = 1"
            ).fetchone()
            if state is None:
                self._insert_initial_state(
                    connection,
                    self.node_id,
                    self.start_position,
                    self.end_position,
                )
                return
            persisted = (
                state["node_id"],
                state["start_position"],
                state["end_position"],
            )
            configured = (self.node_id, self.start_position, self.end_position)
            if persisted != configured:
                raise ReplayStateMismatchError(
                    "configured node/range does not match persisted SQLite state; "
                    "run the explicit host-side reset-replay-state command"
                )

    def next_position(self) -> int:
        with self._connect() as connection:
            row = connection.execute(
                "SELECT next_position FROM replay_state WHERE singleton_id = 1"
            ).fetchone()
        if row is None:
            raise ReplayStoreError("replay state is not initialized")
        return int(row["next_position"])

    def enqueue(
        self, stream_position: int, message_id: str, payload: dict[str, Any]
    ) -> OutboxMessage:
        payload_json = json.dumps(payload, separators=(",", ":"), allow_nan=False)
        queued_at = utc_now()
        with self._connect() as connection:
            connection.execute("BEGIN IMMEDIATE")
            state = connection.execute(
                "SELECT next_position, end_position FROM replay_state "
                "WHERE singleton_id = 1"
            ).fetchone()
            if state is None:
                raise ReplayStoreError("replay state is not initialized")
            if stream_position != state["next_position"]:
                raise ReplayStoreError(
                    f"cannot queue position {stream_position}; expected "
                    f"{state['next_position']}"
                )
            if stream_position > state["end_position"]:
                raise ReplayStoreError("configured replay range is already complete")
            cursor = connection.execute(
                """
                INSERT INTO outbox (
                    stream_position, message_id, payload_json, queued_at
                ) VALUES (?, ?, ?, ?)
                """,
                (stream_position, message_id, payload_json, queued_at),
            )
            connection.execute(
                "UPDATE replay_state SET next_position = ? WHERE singleton_id = 1",
                (stream_position + 1,),
            )
            sequence = int(cursor.lastrowid)
        return OutboxMessage(
            sequence=sequence,
            stream_position=stream_position,
            message_id=message_id,
            payload_json=payload_json,
            attempt_count=0,
            last_error=None,
        )

    def oldest_pending(self) -> OutboxMessage | None:
        with self._connect() as connection:
            row = connection.execute(
                """
                SELECT sequence, stream_position, message_id, payload_json,
                       attempt_count, last_error
                FROM outbox
                ORDER BY sequence
                LIMIT 1
                """
            ).fetchone()
        if row is None:
            return None
        return OutboxMessage(
            sequence=int(row["sequence"]),
            stream_position=int(row["stream_position"]),
            message_id=str(row["message_id"]),
            payload_json=str(row["payload_json"]),
            attempt_count=int(row["attempt_count"]),
            last_error=row["last_error"],
        )

    def record_attempt(self, sequence: int, error: str | None = None) -> None:
        with self._connect() as connection:
            cursor = connection.execute(
                """
                UPDATE outbox
                SET attempt_count = attempt_count + 1, last_error = ?
                WHERE sequence = ?
                """,
                (error, sequence),
            )
            if cursor.rowcount != 1:
                raise ReplayStoreError(f"outbox sequence {sequence} was not found")

    def record_error(self, sequence: int, error: str) -> None:
        with self._connect() as connection:
            connection.execute(
                "UPDATE outbox SET last_error = ? WHERE sequence = ?",
                (error, sequence),
            )

    def acknowledge(self, sequence: int, message_id: str) -> bool:
        with self._connect() as connection:
            cursor = connection.execute(
                "DELETE FROM outbox WHERE sequence = ? AND message_id = ?",
                (sequence, message_id),
            )
        return cursor.rowcount == 1

    def pending_count(self) -> int:
        with self._connect() as connection:
            row = connection.execute("SELECT COUNT(*) AS count FROM outbox").fetchone()
        return int(row["count"])

    def source_complete(self) -> bool:
        return self.next_position() > self.end_position

    @classmethod
    def reset_database(
        cls,
        database_path: Path,
        node_id: int,
        start_position: int,
        end_position: int,
    ) -> None:
        """Atomically replace the logical database with clean replay state."""
        database_path.parent.mkdir(parents=True, exist_ok=True)
        descriptor, temporary_name = tempfile.mkstemp(
            prefix=database_path.name + ".reset-",
            suffix=".sqlite3",
            dir=database_path.parent,
        )
        os.close(descriptor)
        temporary_path = Path(temporary_name)
        replacement = cls(temporary_path, node_id, start_position, end_position)
        try:
            replacement.initialize()
            with temporary_path.open("rb") as database_file:
                os.fsync(database_file.fileno())
            os.replace(temporary_path, database_path)
            directory_fd = os.open(database_path.parent, os.O_DIRECTORY)
            try:
                os.fsync(directory_fd)
            finally:
                os.close(directory_fd)
            for suffix in ("-wal", "-shm", "-journal"):
                sidecar = Path(str(database_path) + suffix)
                if sidecar.exists():
                    sidecar.unlink()
        finally:
            if temporary_path.exists():
                temporary_path.unlink()
