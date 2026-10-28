import json
import sys
import tempfile
import unittest
import uuid
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from test_engine import FakeBackend

from twincat_mcp.backend import Backend
from twincat_mcp.catalog import Catalog
from twincat_mcp.engine import Engine
from twincat_mcp.errors import OperationError

TARGET = "1.2.3.4.1.1"


class ReviewTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.backend = FakeBackend()
        self.engine = Engine(self.directory.name, self.backend)

    def tearDown(self):
        self.engine.close()
        self.directory.cleanup()

    def run_job(self, operation, arguments):
        receipt = self.engine.execute(operation, arguments, str(uuid.uuid4()))
        receipt = self.engine.jobs.wait(receipt["jobHandle"], 4)
        self.assertNotIn(receipt["state"], {"queued", "running"})
        page = self.engine.jobs.result(receipt["jobHandle"], 0, 16000)
        return receipt, json.loads(page["text"])

    def grant(self, operation, **scope):
        _, data = self.run_job(
            "safety.grant", dict(operations=[operation], reason="test authorization", **scope)
        )
        return data["grantHandle"]

    def test_artifact_operations_require_explicit_grants(self):
        for operation, args in [
            ("runtime.record", dict(amsNetId=TARGET, variables=["MAIN.X"])),
            ("scope.create_config", dict(amsNetId=TARGET, variables=["MAIN.X"])),
            ("engineering.generate_library", dict(contextHandle="context")),
        ]:
            with self.subTest(operation=operation), self.assertRaises(OperationError):
                self.engine.execute(operation, args, str(uuid.uuid4()))
        self.assertEqual([], self.backend.calls)

    def test_recording_grant_is_scoped_to_target_and_operation(self):
        grant = self.grant("runtime.record", amsNetId=TARGET)
        for operation, target in [
            ("runtime.record", "2.2.2.2.1.1"),
            ("scope.create_config", TARGET),
        ]:
            receipt, data = self.run_job(
                operation, dict(amsNetId=target, variables=["MAIN.X"], grantHandle=grant)
            )
            self.assertEqual("failed", receipt["state"])
            self.assertEqual("grant_scope_mismatch", data["code"])
        self.assertEqual([], self.backend.calls)

    def test_library_export_uses_context_grant(self):
        solution = Path(self.directory.name) / "Test.sln"
        solution.write_text("")
        _, context = self.run_job(
            "context.open", dict(solutionPath=str(solution), amsNetId=TARGET, plcName="PLC")
        )
        handle = context["contextHandle"]
        bad = self.grant("engineering.activate", contextHandle=handle)
        receipt, data = self.run_job(
            "engineering.generate_library", dict(contextHandle=handle, grantHandle=bad, dryRun=True)
        )
        self.assertEqual("grant_scope_mismatch", data["code"])
        self.assertEqual([], self.backend.calls)
        grant = self.grant("engineering.generate_library", contextHandle=handle)
        receipt, _ = self.run_job(
            "engineering.generate_library",
            dict(contextHandle=handle, grantHandle=grant, dryRun=True),
        )
        self.assertEqual("succeeded", receipt["state"])
        self.assertEqual(1, len(self.backend.calls))

    def test_unqualified_scope_never_dispatches_or_grants(self):
        for operation, args in [
            ("scope.start", dict(configPath="C:/trace.tcscopex", grantHandle="unused")),
            ("scope.export", dict(inputPath="C:/trace.svdx")),
        ]:
            receipt, data = self.run_job(operation, args)
            self.assertEqual("failed", receipt["state"])
            self.assertEqual("unsupported_integration", data["code"])
        _, data = self.run_job(
            "safety.grant",
            dict(operations=["scope.start"], configPath="C:/trace.tcscopex", reason="test"),
        )
        self.assertEqual("unsupported_integration", data["code"])
        self.assertEqual([], self.backend.calls)
        self.backend.scope.start.assert_not_called()

    def test_disabled_operations_are_explicit_and_hidden_from_search(self):
        catalog = self.engine.catalog
        matches = catalog.search(category="scope", limit=20)["matches"]
        self.assertNotIn("scope.start", {m["operation"] for m in matches})
        self.assertFalse(catalog.describe(["scope.start"])["operations"][0]["available"])
        status = self.engine.execute("system.status", {})
        self.assertEqual(
            ["scope.export", "scope.start", "scope.status", "scope.stop"],
            status["unavailableOperations"],
        )

    def test_batch_symbol_names_and_partial_errors_survive_adapter(self):
        native = {
            "Success": False,
            "ErrorCount": 1,
            "Results": {"MAIN.Array[1,2]": {"Success": False, "ErrorMessage": "not found"}},
        }
        with patch("twincat_mcp.backend.run_shell_step", return_value=(native, [])) as run:
            result = Backend().run(
                Catalog().get("runtime.read_var_list"),
                dict(amsNetId=TARGET, symbols=["MAIN.Array[1,2]"]),
            )
        self.assertEqual(["MAIN.Array[1,2]"], run.call_args.args[1]["symbols"])
        self.assertIn("MAIN.Array[1,2]", result["results"])
        self.assertFalse(result["results"]["MAIN.Array[1,2]"]["success"])
        self.assertFalse(result["success"])

    def test_missing_native_success_cannot_hide_failed_batch_items(self):
        with patch("twincat_mcp.backend.run_shell_step", return_value=({"ErrorCount": 1}, [])):
            result = Backend().run(
                Catalog().get("runtime.write_var_list"),
                dict(amsNetId=TARGET, variables={"MAIN.X": "TRUE"}),
            )
        self.assertFalse(result["success"])

    def test_recording_transports_symbol_arrays_without_comma_splitting(self):
        with patch(
            "twincat_mcp.backend.run_tc_automation_with_progress",
            return_value=({"Success": False, "LimitReached": True, "SamplesCollected": 2000}, []),
        ) as run:
            result = Backend().run(
                Catalog().get("runtime.record"),
                dict(amsNetId=TARGET, variables=["MAIN.Array[1,2]"]),
            )
        args = run.call_args.args[1]
        self.assertEqual(["MAIN.Array[1,2]"], json.loads(args[args.index("--variables") + 1]))
        self.assertFalse(result["success"])
        self.assertEqual(2000, result["samplesCollected"])
        self.assertTrue(result["limitReached"])
