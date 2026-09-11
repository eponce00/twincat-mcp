import json
import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path
from unittest.mock import Mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from twincat_mcp.engine import Engine
from twincat_mcp.errors import OperationError
from twincat_mcp.safety import Grants

TARGET = "1.2.3.4.1.1"


class FakeBackend:
    def __init__(self):
        self.calls = []
        self.live = False
        self.ever_started = False
        self.result = {"success": True}
        self.block = None
        self.entered = threading.Event()
        self.scope = Mock()

    def run(self, entry, args, context=None):
        self.calls.append((entry["id"], args, context))
        self.entered.set()
        if self.block:
            self.block.wait(3)
        if entry["engineering"]:
            self.live = self.ever_started = True
        return dict(self.result)

    def alive(self):
        return self.live

    def started(self):
        return self.ever_started

    def close_context(self):
        self.live = self.ever_started = False

    def close(self):
        self.close_context()


class EngineTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.backend = FakeBackend()
        self.engine = Engine(self.folder.name, self.backend)
        self.number = 0
        self.solution = Path(self.folder.name) / "Test.sln"
        self.solution.write_text("")

    def tearDown(self):
        if self.backend.block:
            self.backend.block.set()
        self.engine.close()
        self.folder.cleanup()

    def submit(self, operation, args, key=None):
        self.number += 1
        receipt = self.engine.execute(operation, args, key or f"test-key-{self.number}")
        end = time.monotonic() + 4
        while receipt["state"] not in {"succeeded", "failed", "cancelled", "outcome_unknown"}:
            self.assertLess(time.monotonic(), end)
            time.sleep(0.01)
            receipt = self.engine.jobs.get(receipt["jobHandle"])
        data = json.loads(self.engine.jobs.result(receipt["jobHandle"], 0, 16000)["text"])
        return receipt, data

    def context(self):
        receipt, data = self.submit(
            "context.open", dict(solutionPath=str(self.solution), amsNetId=TARGET, plcName="PLC")
        )
        self.assertEqual("succeeded", receipt["state"])
        return data["contextHandle"]

    def grant(self, operations, **scope):
        receipt, data = self.submit(
            "safety.grant", dict(operations=operations, reason="test authorization", **scope)
        )
        self.assertEqual("succeeded", receipt["state"])
        return data["grantHandle"]

    def test_explicit_target_required_and_octets_validated(self):
        for args in ({}, {"amsNetId": "999.0.0.1.1.1"}):
            with self.assertRaises(OperationError):
                self.engine.execute("runtime.get_state", args, "invalid-target")
        self.assertEqual([], self.backend.calls)

    def test_strict_validation_rejects_extra_field_before_backend(self):
        with self.assertRaises(OperationError):
            self.engine.execute(
                "runtime.get_state", dict(amsNetId=TARGET, executeShell="bad"), "bad-field"
            )
        self.assertEqual([], self.backend.calls)

    def test_context_reserves_target_and_is_exclusive(self):
        handle = self.context()
        receipt, _ = self.submit(
            "context.open", dict(solutionPath=str(self.solution), amsNetId=TARGET, plcName="PLC")
        )
        self.assertEqual("failed", receipt["state"])
        self.submit("engineering.build", dict(contextHandle=handle, clean=False))
        self.assertEqual(TARGET, self.backend.calls[-1][2]["amsNetId"])
        self.assertEqual("PLC", self.backend.calls[-1][2]["plcName"])

    def test_target_cannot_be_overridden_within_context(self):
        handle = self.context()
        with self.assertRaises(OperationError):
            self.engine.execute(
                "engineering.build", dict(contextHandle=handle, amsNetId="2.2.2.2.1.1"), "override"
            )

    def test_worker_loss_does_not_reopen_context(self):
        handle = self.context()
        self.submit("engineering.build", dict(contextHandle=handle))
        self.backend.live = False
        receipt, data = self.submit("engineering.build", dict(contextHandle=handle))
        self.assertEqual("failed", receipt["state"])
        self.assertEqual("worker_lost", data["code"])
        self.assertEqual(1, len(self.backend.calls))

    def test_close_invalidates_handle(self):
        handle = self.context()
        self.submit("context.close", dict(contextHandle=handle))
        receipt, _ = self.submit("engineering.build", dict(contextHandle=handle))
        self.assertEqual("failed", receipt["state"])
        self.assertEqual([], self.backend.calls)

    def test_unauthorized_mutation_is_error_and_never_dispatched(self):
        receipt, data = self.submit(
            "runtime.write_var",
            dict(amsNetId=TARGET, symbol="MAIN.x", value="1", grantHandle="invalid"),
        )
        self.assertEqual("failed", receipt["state"])
        self.assertEqual("grant_required", data["code"])
        self.assertEqual([], self.backend.calls)

    def test_grant_scope_and_operation_enforced(self):
        grant = self.grant(["runtime.write_var"], amsNetId=TARGET)
        for id, args in [
            ("runtime.write_var", dict(amsNetId="2.2.2.2.1.1", symbol="MAIN.x", value="1")),
            ("runtime.set_state", dict(amsNetId=TARGET, state="Run")),
        ]:
            receipt, data = self.submit(id, dict(args, grantHandle=grant))
            self.assertEqual("failed", receipt["state"])
            self.assertEqual("grant_scope_mismatch", data["code"])
        self.assertEqual([], self.backend.calls)

    def test_grant_scope_kind_validated(self):
        receipt, data = self.submit(
            "safety.grant",
            dict(operations=["engineering.activate"], amsNetId=TARGET, reason="test"),
        )
        self.assertEqual("failed", receipt["state"])
        self.assertEqual("invalid_grant", data["code"])

    def test_grant_revocation(self):
        grant = self.grant(["runtime.write_var"], amsNetId=TARGET)
        self.engine.execute("safety.revoke", dict(grantHandle=grant))
        receipt, _ = self.submit(
            "runtime.write_var",
            dict(amsNetId=TARGET, symbol="MAIN.x", value="1", grantHandle=grant),
        )
        self.assertEqual("failed", receipt["state"])

    def test_same_request_key_retrieves_receipt_even_after_revoke(self):
        grant = self.grant(["runtime.write_var"], amsNetId=TARGET)
        args = dict(amsNetId=TARGET, symbol="MAIN.x", value="1", grantHandle=grant)
        first, _ = self.submit("runtime.write_var", args, "write-once")
        self.engine.execute("safety.revoke", dict(grantHandle=grant))
        second, _ = self.submit("runtime.write_var", args, "write-once")
        self.assertEqual(first["jobHandle"], second["jobHandle"])
        self.assertEqual(1, len(self.backend.calls))

    def test_revocation_during_sequence_prevents_next_mutation(self):
        grant = self.grant(["runtime.write_var"], amsNetId=TARGET)
        args = dict(amsNetId=TARGET, symbol="MAIN.x", value="1", grantHandle=grant)
        self.backend.block = threading.Event()
        receipt = self.engine.execute(
            "workflow.sequence",
            dict(steps=[dict(operation="runtime.write_var", arguments=args)] * 2),
            "revoke-during-workflow",
        )
        self.assertTrue(self.backend.entered.wait(1))
        revoked = self.engine.execute("safety.revoke", dict(grantHandle=grant))
        self.assertTrue(revoked["success"])
        self.backend.block.set()
        self.engine.jobs.pending.join()
        final = self.engine.jobs.get(receipt["jobHandle"])
        self.assertEqual("failed", final["state"])
        self.assertEqual(1, final["result"]["completedSteps"])
        self.assertEqual(1, len(self.backend.calls))

    def test_control_submission_is_deduplicated(self):
        args = dict(solutionPath=str(self.solution), amsNetId=TARGET, plcName="PLC")
        first, data = self.submit("context.open", args, "open-once")
        second, again = self.submit("context.open", args, "open-once")
        self.assertEqual(first["jobHandle"], second["jobHandle"])
        self.assertEqual(data, again)

    def test_key_conflict_is_not_executed(self):
        self.submit("runtime.get_state", dict(amsNetId=TARGET), "reuse-key")
        with self.assertRaises(OperationError):
            self.engine.execute("runtime.get_state", dict(amsNetId="2.2.2.2.1.1"), "reuse-key")
        self.assertEqual(1, len(self.backend.calls))

    def test_sequence_validates_all_steps_before_effects(self):
        with self.assertRaises(OperationError):
            self.engine.execute(
                "workflow.sequence",
                dict(
                    steps=[
                        dict(operation="runtime.get_state", arguments=dict(amsNetId=TARGET)),
                        dict(operation="runtime.write_var", arguments={}),
                    ]
                ),
                "invalid-sequence",
            )
        self.assertEqual([], self.backend.calls)

    def test_sequence_checks_grants_for_entire_plan_before_effects(self):
        receipt, data = self.submit(
            "workflow.sequence",
            dict(
                steps=[
                    dict(operation="runtime.get_state", arguments=dict(amsNetId=TARGET)),
                    dict(
                        operation="runtime.write_var_list",
                        arguments=dict(
                            amsNetId=TARGET, variables={"MAIN.x": "1"}, grantHandle="bad"
                        ),
                    ),
                ]
            ),
        )
        self.assertEqual("failed", receipt["state"])
        self.assertEqual([], self.backend.calls)

    def test_sequence_disallows_controls_and_nesting(self):
        for id in ["context.open", "workflow.sequence"]:
            with self.assertRaises(OperationError):
                self.engine.execute(
                    "workflow.sequence",
                    dict(steps=[dict(operation=id, arguments={})]),
                    "nested-test",
                )

    def test_sequence_stops_on_failure_and_preserves_receipt(self):
        self.backend.result = {
            "success": False,
            "errorMessage": "compile failed",
            "errors": [{"line": 42}],
        }
        receipt, data = self.submit(
            "workflow.sequence",
            dict(
                steps=[
                    dict(operation="runtime.get_state", arguments=dict(amsNetId=TARGET)),
                    dict(operation="runtime.get_state", arguments=dict(amsNetId=TARGET)),
                ]
            ),
        )
        self.assertEqual("failed", receipt["state"])
        self.assertEqual(1, len(self.backend.calls))
        self.assertEqual(42, data["steps"][0]["result"]["errors"][0]["line"])

    def test_queue_is_serial_and_cancelled_queued_work_never_runs(self):
        self.backend.block = threading.Event()
        first = self.engine.execute("runtime.get_state", dict(amsNetId=TARGET), "block-first")
        self.assertTrue(self.backend.entered.wait(1))
        second = self.engine.execute("runtime.get_state", dict(amsNetId=TARGET), "block-second")
        self.engine.execute("job.cancel", dict(jobHandle=second["jobHandle"]))
        start = time.monotonic()
        self.assertEqual(
            "running", self.engine.execute("job.get", dict(jobHandle=first["jobHandle"]))["state"]
        )
        self.assertLess(time.monotonic() - start, 0.1)
        self.backend.block.set()
        self.engine.jobs.pending.join()
        self.assertEqual("cancelled", self.engine.jobs.get(second["jobHandle"])["state"])
        self.assertEqual(1, len(self.backend.calls))

    def test_cancellation_of_running_command_keeps_actual_outcome(self):
        self.backend.block = threading.Event()
        receipt = self.engine.execute("runtime.get_state", dict(amsNetId=TARGET), "running-cancel")
        self.assertTrue(self.backend.entered.wait(1))
        self.engine.jobs.cancel(receipt["jobHandle"])
        self.backend.block.set()
        self.engine.jobs.pending.join()
        final = self.engine.jobs.get(receipt["jobHandle"])
        self.assertEqual("succeeded", final["state"])
        self.assertTrue(final["cancelRequested"])

    def test_large_results_are_paged_without_silent_loss(self):
        self.backend.result = {"success": True, "text": "unicode Ω" * 3000}
        receipt = self.engine.execute("runtime.get_state", dict(amsNetId=TARGET), "large-result")
        self.engine.jobs.pending.join()
        self.assertNotIn("result", self.engine.jobs.get(receipt["jobHandle"]))
        offset = 0
        chunks = []
        while True:
            page = self.engine.jobs.result(receipt["jobHandle"], offset, 1000)
            chunks.append(page["text"])
            if page["nextOffset"] is None:
                break
            offset = page["nextOffset"]
        self.assertEqual(self.backend.result, json.loads("".join(chunks)))

    def test_unknown_outcome_is_persisted_and_not_replayed(self):
        self.backend.result = {"success": False, "outcomeUnknown": True, "dispatched": True}
        first, _ = self.submit("runtime.get_state", dict(amsNetId=TARGET), "uncertain-job")
        second, _ = self.submit("runtime.get_state", dict(amsNetId=TARGET), "uncertain-job")
        self.assertEqual("outcome_unknown", first["state"])
        self.assertEqual(first["jobHandle"], second["jobHandle"])
        self.assertEqual(1, len(self.backend.calls))

    def test_receipt_survives_engine_restart(self):
        first, data = self.submit("runtime.get_state", dict(amsNetId=TARGET), "persisted-job")
        self.engine.close()
        self.engine = Engine(self.folder.name, self.backend)
        second, again = self.submit("runtime.get_state", dict(amsNetId=TARGET), "persisted-job")
        self.assertEqual(first["jobHandle"], second["jobHandle"])
        self.assertEqual(data, again)
        self.assertEqual(1, len(self.backend.calls))

    def test_wait_state_owns_polling_loop(self):
        states = iter(["Config", "Run"])

        def run(entry, args, context):
            self.backend.calls.append((entry["id"], args, context))
            return {"success": True, "adsState": next(states)}

        self.backend.run = run
        receipt, data = self.submit(
            "workflow.wait_state", dict(amsNetId=TARGET, expectedState="Run", intervalSeconds=0.1)
        )
        self.assertEqual("succeeded", receipt["state"])
        self.assertEqual(2, data["attempts"])


class GrantTests(unittest.TestCase):
    def test_fixed_expiration_is_not_extended_by_use(self):
        now = [1.0]
        grants = Grants(lambda: now[0])
        args = dict(operations=["runtime.write_var"], amsNetId=TARGET, ttlSeconds=10)
        token = grants.create(args)["grantHandle"]
        request = dict(amsNetId=TARGET, grantHandle=token)
        now[0] = 10
        grants.check("runtime.write_var", request)
        now[0] = 11
        with self.assertRaises(OperationError):
            grants.check("runtime.write_var", request)
