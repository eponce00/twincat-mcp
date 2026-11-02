"""Concurrent ADS recording jobs, using a fake backend and temporary SQLite only."""

import hashlib
import json
import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from test_engine import FakeBackend

from twincat_mcp.engine import Engine
from twincat_mcp.errors import OperationError

TARGET = "1.2.3.4.1.1"


class RecordingBackend(FakeBackend):
    def __init__(self):
        super().__init__()
        self.record_entered = threading.Event()
        self.record_release = threading.Event()
        self.normal_entered = threading.Event()
        self.normal_release = threading.Event()
        self.normal_release.set()
        self.count_lock = threading.Lock()
        self.active = dict(record=0, normal=0)
        self.maximum = dict(record=0, normal=0)
        self.record_calls = 0
        self.threads = {}
        self.record_result = dict(success=True, outputPath="fake.csv", samples=3)
        self.record_failure = None

    def run(self, entry, args, context=None, progress=None):
        recording = entry["id"] == "runtime.record"
        lane = "record" if recording else "normal"
        with self.count_lock:
            self.calls.append((entry["id"], args, context))
            self.threads[entry["id"]] = threading.current_thread().name
            self.active[lane] += 1
            self.maximum[lane] = max(self.maximum[lane], self.active[lane])
            if recording:
                self.record_calls += 1
        try:
            entered = self.record_entered if recording else self.normal_entered
            release = self.record_release if recording else self.normal_release
            entered.set()
            if not release.wait(5):
                raise AssertionError("Test did not release the fake backend")
            if recording:
                if self.record_failure:
                    raise self.record_failure
                return dict(self.record_result)
            return dict(success=True, value="fake value")
        finally:
            with self.count_lock:
                self.active[lane] -= 1


class RecordQueueTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory(prefix="record-queue-fake-")
        self.backend = RecordingBackend()
        self.engine = Engine(self.folder.name, self.backend)
        grant = self.finish(self.engine.execute(
            "safety.grant", dict(operations=["runtime.record"], amsNetId=TARGET, reason="fake test"),
            "record-grant",
        ))["result"]["grantHandle"]
        self.record_args = dict(amsNetId=TARGET, variables=["MAIN.value"], grantHandle=grant)
        self.read_args = dict(amsNetId=TARGET, symbol="MAIN.value")

    def tearDown(self):
        self.backend.record_release.set()
        self.backend.normal_release.set()
        if self.engine is not None:
            self.engine.close()
        self.folder.cleanup()

    def finish(self, receipt):
        deadline = time.monotonic() + 2
        while True:
            current = self.engine.execute("job.get", dict(jobHandle=receipt["jobHandle"]))
            if current["state"] in {"succeeded", "failed", "cancelled", "outcome_unknown"}:
                return current
            self.assertLess(time.monotonic(), deadline, "Fake job blocked on the other queue")
            time.sleep(0.005)

    def record(self, key):
        return self.engine.execute("runtime.record", self.record_args, key)

    def read(self, key):
        return self.engine.execute("runtime.read_var", self.read_args, key)

    def start_recording(self):
        first = self.record("first-recording")
        self.assertTrue(self.backend.record_entered.wait(1))
        return first

    def test_blocked_recording_does_not_block_read_or_authorized_write(self):
        grant = self.finish(self.engine.execute(
            "safety.grant", dict(
                operations=["runtime.write_var"], amsNetId=TARGET, reason="isolated fake test"
            ), "fake-write-grant"
        ))["result"]["grantHandle"]
        recording = self.start_recording()
        read = self.read("read-during-recording")
        write = self.engine.execute(
            "runtime.write_var", dict(self.read_args, value="7", grantHandle=grant),
            "write-during-recording",
        )
        self.assertEqual("succeeded", self.finish(read)["state"])
        self.assertEqual("succeeded", self.finish(write)["state"])
        self.assertEqual("running", self.engine.jobs.get(recording["jobHandle"])["state"])
        self.assertFalse(self.backend.record_release.is_set())
        self.assertEqual("twincat-recording", self.backend.threads["runtime.record"])
        self.assertEqual("twincat-operations", self.backend.threads["runtime.write_var"])

    def test_recording_starts_during_normal_work_and_normal_jobs_stay_serial(self):
        self.backend.normal_release.clear()
        first = self.read("normal-first")
        self.assertTrue(self.backend.normal_entered.wait(1))
        self.start_recording()
        second = self.read("normal-second")
        self.assertEqual("queued", self.engine.jobs.get(second["jobHandle"])["state"])
        self.assertEqual(1, self.backend.active["normal"])
        self.backend.normal_release.set()
        self.assertEqual("succeeded", self.finish(first)["state"])
        self.assertEqual("succeeded", self.finish(second)["state"])
        self.assertEqual(1, self.backend.maximum["normal"])
        self.assertEqual(1, self.backend.active["record"])

    def test_recordings_are_serial_and_status_counts_both_queues(self):
        first = self.start_recording()
        second = self.record("second-recording")
        self.backend.normal_release.clear()
        normal = self.read("blocked-normal")
        self.assertTrue(self.backend.normal_entered.wait(1))
        queued = self.read("queued-normal")
        self.assertEqual("queued", self.engine.jobs.get(second["jobHandle"])["state"])
        self.assertEqual(1, self.backend.record_calls)
        self.assertEqual(2, self.engine.execute("system.status", {})["queued"])
        self.backend.record_release.set()
        self.backend.normal_release.set()
        for receipt in (first, second, normal, queued):
            self.assertEqual("succeeded", self.finish(receipt)["state"])
        self.assertEqual(2, self.backend.record_calls)
        self.assertEqual(1, self.backend.maximum["record"])

    def test_running_and_queued_recordings_deduplicate_and_conflicts_fail(self):
        first = self.start_recording()
        second = self.record("second-recording")
        self.assertEqual(first["jobHandle"], self.record("first-recording")["jobHandle"])
        self.assertEqual(second["jobHandle"], self.record("second-recording")["jobHandle"])
        with self.assertRaises(OperationError) as conflict:
            self.engine.execute(
                "runtime.record", dict(self.record_args, variables=["MAIN.other"]),
                "first-recording",
            )
        self.assertEqual("request_key_conflict", conflict.exception.code)
        self.backend.record_release.set()
        self.finish(first)
        self.finish(second)
        self.assertEqual(2, self.backend.record_calls)

    def test_aggregate_queue_capacity_and_deduplication(self):
        first = self.start_recording()
        self.backend.normal_release.clear()
        self.read("normal-running")
        self.assertTrue(self.backend.normal_entered.wait(1))
        for index in range(32):
            self.record(f"record-{index}")
            self.read(f"read-{index}")
        self.assertEqual(64, self.engine.execute("system.status", {})["queued"])
        self.assertEqual(first["jobHandle"], self.record("first-recording")["jobHandle"])
        for operation, args in [
            ("runtime.record", self.record_args), ("runtime.read_var", self.read_args)
        ]:
            with self.subTest(operation=operation), self.assertRaises(OperationError) as full:
                self.engine.execute(operation, args, "retry-" + operation)
            self.assertEqual("queue_full", full.exception.code)

    def test_queued_recording_rechecks_revoked_grant(self):
        first = self.start_recording()
        second = self.record("revoked-recording")
        self.engine.execute("safety.revoke", dict(grantHandle=self.record_args["grantHandle"]))
        self.assertEqual(first["jobHandle"], self.record("first-recording")["jobHandle"])
        self.backend.record_release.set()
        self.finish(first)
        result = self.finish(second)
        self.assertEqual("failed", result["state"])
        self.assertEqual("grant_required", result["result"]["code"])
        self.assertEqual(1, self.backend.record_calls)

    def test_wrong_target_or_operation_grants_never_reach_recorder(self):
        grants = [
            self.finish(self.engine.execute(
                "safety.grant",
                dict(operations=["runtime.record"], amsNetId="2.2.2.2.1.1", reason="fake test"),
                "wrong-target",
            ))["result"]["grantHandle"],
            self.finish(self.engine.execute(
                "safety.grant",
                dict(operations=["runtime.write_var"], amsNetId=TARGET, reason="fake test"),
                "wrong-operation",
            ))["result"]["grantHandle"],
        ]
        for index, grant in enumerate(grants):
            receipt = self.engine.execute(
                "runtime.record", dict(self.record_args, grantHandle=grant), f"bad-grant-{index}"
            )
            result = self.finish(receipt)
            self.assertEqual("failed", result["state"])
            self.assertEqual("grant_scope_mismatch", result["result"]["code"])
        self.assertEqual(0, self.backend.record_calls)

    def test_cancelled_queued_recording_is_never_dispatched(self):
        first = self.start_recording()
        second = self.record("cancelled-recording")
        self.engine.execute("job.cancel", dict(jobHandle=second["jobHandle"]))
        self.assertEqual("succeeded", self.finish(self.read("read-after-cancel"))["state"])
        self.backend.record_release.set()
        self.finish(first)
        self.assertEqual("cancelled", self.finish(second)["state"])
        self.assertEqual(1, self.backend.record_calls)

    def test_recording_result_paging_and_deduplication_survive_restart(self):
        self.backend.record_result = dict(success=True, data="recorded Ω" * 1000)
        self.backend.record_release.set()
        original = self.finish(self.record("persisted-recording"))
        self.assertNotIn("result", original)
        self.engine.close()
        self.engine = Engine(self.folder.name, self.backend)
        again = self.record("persisted-recording")
        self.assertEqual(original, again)
        offset, chunks = 0, []
        while True:
            page = self.engine.execute(
                "job.result", dict(jobHandle=again["jobHandle"], offset=offset, limit=1000)
            )
            chunks.append(page["text"])
            if page["nextOffset"] is None:
                break
            offset = page["nextOffset"]
        self.assertEqual(self.backend.record_result, json.loads("".join(chunks)))
        self.assertEqual(1, self.backend.record_calls)

    def test_recording_exception_does_not_terminate_record_worker(self):
        self.backend.record_failure = RuntimeError("fake recorder failure")
        self.backend.record_release.set()
        first = self.finish(self.record("failed-recording"))
        self.assertEqual("outcome_unknown", first["state"])
        self.backend.record_failure = None
        self.assertEqual("succeeded", self.finish(self.record("next-recording"))["state"])
        self.assertEqual(1, self.backend.maximum["record"])

    def test_sequence_recording_is_rejected_before_queue_or_backend(self):
        with self.assertRaisesRegex(OperationError, "separate job"):
            self.engine.execute("workflow.sequence", dict(steps=[
                dict(operation="runtime.read_var", arguments=self.read_args),
                dict(operation="runtime.record", arguments=self.record_args),
            ]), "recording-in-sequence")
        self.assertEqual([], self.backend.calls)
        self.assertEqual(0, self.engine.execute("system.status", {})["queued"])

    def test_historical_record_sequence_receipt_still_deduplicates(self):
        # A terminal receipt created by the previous release remains inspectable,
        # although new recording sequences must use their own recording jobs.
        operation, key, handle = "workflow.sequence", "legacy-record-sequence", "legacy-handle"
        args = self.engine.catalog.validate(operation, dict(steps=[
            dict(operation="runtime.record", arguments=self.record_args)
        ]))
        encoded = json.dumps([operation, args], sort_keys=True, separators=(",", ":"), ensure_ascii=False)
        fingerprint = hashlib.sha256(encoded.encode()).hexdigest()
        result = dict(success=True, completedSteps=1)
        with self.engine.jobs.lock:
            self.engine.jobs.db.execute(
                "INSERT INTO jobs VALUES (?,?,?,?,?,?,?,?,?,?,?)",
                (handle, key, fingerprint, operation, self.engine.jobs.owner, "succeeded",
                 time.time(), time.time(), "[]", json.dumps(result), 0),
            )
            self.engine.jobs.db.commit()
        receipt = self.engine.execute(operation, args, key)
        self.assertEqual(handle, receipt["jobHandle"])
        self.assertEqual(result, receipt["result"])
        self.assertEqual([], self.backend.calls)
        with self.assertRaisesRegex(OperationError, "separate job"):
            self.engine.execute(operation, args, "new-record-sequence")

    def test_shutdown_retains_database_and_backend_until_record_worker_stops(self):
        recording = self.start_recording()
        queued_record = self.record("shutdown-queued-record")
        jobs = self.engine.jobs
        with (
            patch.object(jobs.record_worker, "join"),
            patch.object(self.backend, "close", wraps=self.backend.close) as close,
        ):
            self.engine.close()
            self.assertTrue(jobs.record_worker.is_alive())
            with jobs.lock:
                self.assertEqual(1, jobs.db.execute("SELECT 1").fetchone()[0])
            close.assert_not_called()
            self.assertEqual("cancelled", jobs.get(queued_record["jobHandle"])["state"])
            self.backend.record_release.set()
        jobs.record_worker.join(timeout=2)
        self.assertFalse(jobs.record_worker.is_alive())
        self.assertEqual("outcome_unknown", jobs.get(recording["jobHandle"])["state"])
        self.engine.close()
        self.engine = None
