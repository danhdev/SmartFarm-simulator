from __future__ import annotations

import json
from pathlib import Path
import sqlite3
import tempfile
import unittest

from replay_store import (
    ReplayAlreadyRunningError,
    ReplayProcessLock,
    ReplayStateMismatchError,
    ReplayStore,
)


class ReplayStoreTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary_directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary_directory.cleanup)
        self.database_path = Path(self.temporary_directory.name) / "state.sqlite3"
        self.store = ReplayStore(self.database_path, 6, 1, 3)
        self.store.initialize()

    def test_enqueue_atomically_persists_payload_and_advances_cursor(self) -> None:
        payload = {"message_id": "uuid-1", "value": 42}

        queued = self.store.enqueue(1, "uuid-1", payload)
        restarted_store = ReplayStore(self.database_path, 6, 1, 3)
        restarted_store.initialize()
        pending = restarted_store.oldest_pending()

        self.assertEqual(2, restarted_store.next_position())
        self.assertIsNotNone(pending)
        assert pending is not None
        self.assertEqual(queued.sequence, pending.sequence)
        self.assertEqual("uuid-1", pending.message_id)
        self.assertEqual(payload, json.loads(pending.payload_json))

    def test_duplicate_insert_rolls_back_cursor_advance(self) -> None:
        self.store.enqueue(1, "same-id", {"message_id": "same-id", "row": 1})

        with self.assertRaises(sqlite3.IntegrityError):
            self.store.enqueue(2, "same-id", {"message_id": "same-id", "row": 2})

        self.assertEqual(2, self.store.next_position())
        self.assertEqual(1, self.store.pending_count())

    def test_oldest_pending_and_acknowledge_enforce_fifo(self) -> None:
        first = self.store.enqueue(1, "uuid-1", {"message_id": "uuid-1"})
        second = self.store.enqueue(2, "uuid-2", {"message_id": "uuid-2"})

        self.assertEqual(first.sequence, self.store.oldest_pending().sequence)
        self.assertFalse(self.store.acknowledge(first.sequence, "wrong-id"))
        self.assertEqual(2, self.store.pending_count())
        self.assertTrue(self.store.acknowledge(first.sequence, "uuid-1"))
        self.assertEqual(second.sequence, self.store.oldest_pending().sequence)

    def test_attempt_failure_is_persisted_without_removing_message(self) -> None:
        message = self.store.enqueue(1, "uuid-1", {"message_id": "uuid-1"})

        self.store.record_attempt(message.sequence)
        self.store.record_error(message.sequence, "broker unavailable")
        pending = self.store.oldest_pending()

        self.assertIsNotNone(pending)
        assert pending is not None
        self.assertEqual(1, pending.attempt_count)
        self.assertEqual("broker unavailable", pending.last_error)
        self.assertEqual("uuid-1", pending.message_id)

    def test_initialize_rejects_changed_node_or_range(self) -> None:
        mismatches = [(18, 1, 3), (6, 2, 3), (6, 1, 2)]

        for node_id, start, end in mismatches:
            with self.subTest(node_id=node_id, start=start, end=end):
                with self.assertRaisesRegex(
                    ReplayStateMismatchError, "reset-replay-state"
                ):
                    ReplayStore(
                        self.database_path, node_id, start, end
                    ).initialize()

    def test_reset_database_clears_outbox_and_reinitializes_start_position(self) -> None:
        self.store.enqueue(1, "uuid-1", {"message_id": "uuid-1"})

        ReplayStore.reset_database(self.database_path, 6, 2, 3)
        reset_store = ReplayStore(self.database_path, 6, 2, 3)
        reset_store.initialize()

        self.assertEqual(2, reset_store.next_position())
        self.assertEqual(0, reset_store.pending_count())
        self.assertIsNone(reset_store.oldest_pending())

    def test_second_process_lock_refuses_while_first_is_held(self) -> None:
        with ReplayProcessLock(self.database_path):
            with self.assertRaisesRegex(ReplayAlreadyRunningError, "already running"):
                with ReplayProcessLock(self.database_path):
                    self.fail("second lock must not be acquired")

        with ReplayProcessLock(self.database_path):
            self.assertTrue(self.database_path.with_name("state.sqlite3.lock").exists())


if __name__ == "__main__":
    unittest.main()
