"""workflow.deploy ending in Run and its optional HMI publish; engineering.restart and runtime.set_state contracts.
No TwinCAT, XAE, PLC or HMI server is used: the native steps are mocked."""

import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from twincat_mcp.backend import Backend  # noqa: E402
from twincat_mcp.catalog import Catalog  # noqa: E402
from twincat_mcp.engine import Engine  # noqa: E402
from twincat_mcp.errors import OperationError  # noqa: E402

TARGET = "1.2.3.4.1.1"
FINGERPRINT = "a" * 64


class FakeBackend:
    """Records calls; never starts a worker."""

    def __init__(self):
        self.calls = []

    def run(self, entry, args, context=None):
        self.calls.append((entry["id"], args, context))
        return {"success": True}

    def alive(self):
        return False

    def started(self):
        return False

    def close_context(self):
        pass

    def close(self):
        pass


class DeployRunAndPublish(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        root = Path(self.folder.name)
        self.solution = root / "Station.sln"
        self.solution.write_text("")
        self.project = root / "Hmi.hmiproj"
        self.project.write_text("")
        self.ctx = dict(
            solutionPath=str(self.solution), amsNetId=TARGET, plcName="PLC1", tcVersion=None
        )
        self.spec = dict(
            projectPath=str(self.project),
            targetUrl="https://10.0.0.5:2020",
            profileName="Station",
            expectedFingerprint=FINGERPRINT,
        )

    def tearDown(self):
        self.folder.cleanup()

    def run_deploy(self, deploy_result, publish_result=None, **extra):
        calls = []

        def step(command, args, **kwargs):
            calls.append((command, dict(args), kwargs))
            return (deploy_result if command == "deploy" else publish_result), []

        with patch("twincat_mcp.backend.run_shell_step", side_effect=step):
            result = Backend().run(
                Catalog().get("workflow.deploy"), dict(hmiPublish=self.spec, **extra), self.ctx
            )
        return result, calls

    def test_publish_runs_once_after_run_is_verified_in_an_hmi_only_load(self):
        result, calls = self.run_deploy(
            {"Success": True, "Run": {"ReachedRun": True}}, {"Success": True, "published": True}
        )
        self.assertEqual(["deploy", "hmi-publish"], [c[0] for c in calls])
        self.assertNotIn("hmiPublish", calls[0][1], "the native deploy never sees the HMI step")
        command, args, kwargs = calls[1]
        self.assertTrue(kwargs["hmi_only"])
        self.assertEqual("https://10.0.0.5:2020/", args["targetUrl"])
        self.assertEqual(FINGERPRINT, args["expectedFingerprint"])
        self.assertEqual(str(self.solution), kwargs["solution_path"])
        self.assertTrue(result["success"])
        self.assertTrue(result["hmiPublish"]["success"])

    def test_no_publish_when_the_deployment_did_not_reach_run(self):
        result, calls = self.run_deploy(
            {
                "Success": False,
                "ErrorMessage": "The target started in Config and stayed there for 30 s",
            }
        )
        self.assertEqual(["deploy"], [c[0] for c in calls])
        self.assertFalse(result["success"])
        self.assertFalse(result["hmiPublish"]["published"])
        self.assertIn("did not end in Run", result["hmiPublish"]["reason"])

    def test_dry_run_plans_the_publish_without_running_it(self):
        result, calls = self.run_deploy({"Success": True}, dryRun=True)
        self.assertEqual(["deploy"], [c[0] for c in calls])
        self.assertTrue(result["hmiPublish"]["planned"])
        self.assertEqual("Station", result["hmiPublish"]["profileName"])

    def test_failed_publish_fails_the_workflow_but_keeps_the_deploy_receipt(self):
        result, _ = self.run_deploy(
            {"Success": True, "Run": {"ReachedRun": True}},
            {"Success": False, "ErrorMessage": "fingerprint changed"},
        )
        self.assertFalse(result["success"])
        self.assertIn("reached Run, but the HMI was not published", result["errorMessage"])
        self.assertTrue(result["run"]["reachedRun"])

    def test_uncertain_publish_marks_the_workflow_uncertain(self):
        result, _ = self.run_deploy(
            {"Success": True},
            {"Success": False, "outcomeUnknown": True, "errorMessage": "lost reply"},
        )
        self.assertTrue(result["outcomeUnknown"])


class DeployPublishGrant(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.backend = FakeBackend()
        self.engine = Engine(self.folder.name, self.backend)
        root = Path(self.folder.name)
        (root / "Test.sln").write_text("")
        (root / "Hmi.hmiproj").write_text("")
        self.handle = self.engine.control(
            "context.open",
            dict(solutionPath=str(root / "Test.sln"), amsNetId=TARGET, plcName="PLC"),
        )["contextHandle"]
        self.spec = dict(
            projectPath=str(root / "Hmi.hmiproj"),
            targetUrl="https://10.0.0.5",
            profileName="Station",
            expectedFingerprint=FINGERPRINT,
        )

    def tearDown(self):
        self.engine.close()
        self.folder.cleanup()

    def args(self, operations, **spec):
        grant = self.engine.grants.create(dict(operations=operations, contextHandle=self.handle))[
            "grantHandle"
        ]
        return dict(
            contextHandle=self.handle,
            grantHandle=grant,
            confirm="CONFIRM",
            hmiPublish=dict(self.spec, **spec),
        )

    def test_publish_needs_hmi_publish_in_the_same_grant(self):
        with self.assertRaises(OperationError) as refused:
            self.engine.preflight("workflow.deploy", self.args(["workflow.deploy"]))
        self.assertEqual("grant_scope_mismatch", refused.exception.code)
        self.engine.preflight("workflow.deploy", self.args(["workflow.deploy", "hmi.publish"]))

    def test_hmi_target_is_validated_before_anything_runs(self):
        with self.assertRaises(OperationError):
            self.engine.preflight(
                "workflow.deploy",
                self.args(["workflow.deploy", "hmi.publish"], targetUrl="http://10.0.0.5"),
            )
        self.assertEqual([], self.backend.calls)


class RestartAndStateContracts(unittest.TestCase):
    def test_run_timeouts_are_bounded(self):
        for id in ("workflow.deploy", "engineering.restart"):
            spec = Catalog().get(id)["inputSchema"]["properties"]["runTimeoutSeconds"]
            self.assertEqual((10, 600, 120), (spec["minimum"], spec["maximum"], spec["default"]))

    def test_restart_failure_is_a_failed_receipt(self):
        native = {
            "Success": False,
            "ErrorMessage": "The target started in Config and stayed there for 30 s. Target messages: [Error] x",
            "Run": {"ReachedRun": False, "SystemState": "Config"},
        }
        with patch("twincat_mcp.backend.run_shell_step", return_value=(native, [])):
            result = Backend().run(
                Catalog().get("engineering.restart"),
                {},
                dict(solutionPath="s.sln", amsNetId=TARGET, plcName="PLC"),
            )
        self.assertFalse(result["success"])
        self.assertEqual("Config", result["run"]["systemState"])

    def test_set_state_documents_the_restart_into_run(self):
        self.assertIn("Reset", Catalog().get("runtime.set_state")["description"])


if __name__ == "__main__":
    unittest.main()
