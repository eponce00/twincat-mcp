import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from test_engine import FakeBackend

from twincat_mcp.backend import Backend
from twincat_mcp.catalog import Catalog
from twincat_mcp.engine import Engine
from twincat_mcp.errors import OperationError


class HmiTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.solution = self.root / "Machine.sln"
        self.project = self.root / "Panel.hmiproj"
        self.solution.write_text("")
        self.project.write_text("<Project/>")
        self.backend = FakeBackend()
        self.engine = Engine(self.root / "state", self.backend)
        self.serial = 0

    def tearDown(self):
        self.engine.close()
        self.tmp.cleanup()

    def submit(self, op, args, key=None):
        self.serial += 1
        receipt = self.engine.execute(op, args, key or str(self.serial))
        while receipt["state"] in {"queued", "running"}:
            receipt = self.engine.jobs.wait(receipt["jobHandle"], 2)
        return receipt, json.loads(self.engine.jobs.result(receipt["jobHandle"], 0, 16000)["text"])

    def context(self):
        return self.submit(
            "context.open_hmi",
            dict(
                solutionPath=str(self.solution),
                projectPath=str(self.project),
                targetUrl="https://LOCALHOST:443/",
            ),
        )[1]["contextHandle"]

    def grant(self, ctx, op="hmi.publish"):
        return self.submit(
            "safety.grant", dict(contextHandle=ctx, operations=[op], reason="authorized test")
        )[1]["grantHandle"]

    def publish_args(self, ctx):
        return dict(
            contextHandle=ctx,
            grantHandle=self.grant(ctx),
            confirm="CONFIRM",
            profileName="Local",
            expectedFingerprint="a" * 64,
        )

    def test_hmi_context_binds_exact_project_and_url(self):
        ctx = self.context()
        self.submit("hmi.inspect", dict(contextHandle=ctx))
        self.assertEqual("hmi", self.backend.calls[-1][2]["kind"])
        self.assertEqual("https://localhost/", self.backend.calls[-1][2]["targetUrl"])
        self.assertNotIn("amsNetId", self.backend.calls[-1][2])

    def test_bad_targets_rejected_before_persisting_a_job(self):
        for url in [
            "http://host/",
            "https://u:secret@host/",
            "https://host/?key=secret",
            "https://host/path",
            "https://host/#x",
            "https://host/\n",
        ]:
            with self.assertRaises(OperationError):
                self.engine.execute(
                    "context.open_hmi",
                    dict(
                        solutionPath=str(self.solution),
                        projectPath=str(self.project),
                        targetUrl=url,
                    ),
                    "bad-url",
                )
        self.assertIsNone(self.engine.context)

    def test_missing_hmi_project_refused(self):
        receipt, result = self.submit(
            "context.open_hmi",
            dict(
                solutionPath=str(self.solution),
                projectPath=str(self.root / "absent.hmiproj"),
                targetUrl="https://host/",
            ),
        )
        self.assertEqual("failed", receipt["state"])
        self.assertEqual("invalid_hmi_project", result["code"])

    def test_plc_operation_cannot_use_hmi_context(self):
        ctx = self.context()
        receipt, result = self.submit("engineering.build", dict(contextHandle=ctx))
        self.assertEqual("failed", receipt["state"])
        self.assertEqual("wrong_context_kind", result["code"])
        self.assertFalse(self.backend.calls)

    def test_hmi_operation_cannot_use_plc_context(self):
        ctx = self.submit(
            "context.open",
            dict(solutionPath=str(self.solution), amsNetId="1.2.3.4.1.1", plcName="PLC"),
        )[1]["contextHandle"]
        self.assertEqual(
            "wrong_context_kind", self.submit("hmi.inspect", dict(contextHandle=ctx))[1]["code"]
        )

    def test_context_is_exclusive_across_plc_and_hmi(self):
        self.context()
        receipt, result = self.submit(
            "context.open",
            dict(solutionPath=str(self.solution), amsNetId="1.2.3.4.1.1", plcName="PLC"),
        )
        self.assertEqual("failed", receipt["state"])
        self.assertEqual("context_busy", result["code"])

    def test_publish_requires_its_own_context_grant(self):
        ctx = self.context()
        args = self.publish_args(ctx)
        self.engine.grants.revoke(args["grantHandle"])
        self.assertEqual("failed", self.submit("hmi.publish", args)[0]["state"])
        self.assertFalse(self.backend.calls)

    def test_build_grant_does_not_authorize_publish(self):
        ctx = self.context()
        args = self.publish_args(ctx)
        args["grantHandle"] = self.grant(ctx, "hmi.build")
        self.assertEqual("failed", self.submit("hmi.publish", args)[0]["state"])
        self.assertFalse(self.backend.calls)

    def test_publish_deduplicates_even_after_grant_revocation(self):
        ctx = self.context()
        args = self.publish_args(ctx)
        first = self.submit("hmi.publish", args, "publish-once")
        self.engine.grants.revoke(args["grantHandle"])
        second = self.submit("hmi.publish", args, "publish-once")
        self.assertEqual(first[0]["jobHandle"], second[0]["jobHandle"])
        self.assertEqual(1, len(self.backend.calls))

    def test_uncertain_publish_only_allows_reading_status(self):
        ctx = self.context()
        args = self.publish_args(ctx)
        self.backend.result = dict(success=False, outcomeUnknown=True)
        self.submit("hmi.publish", args)
        self.backend.result = dict(success=True, isPublishing=False)
        self.assertTrue(self.submit("hmi.status", dict(contextHandle=ctx))[1]["success"])
        self.assertEqual("failed", self.submit("hmi.publish", args)[0]["state"])
        self.assertEqual(2, len(self.backend.calls))

    def test_no_password_target_override_or_code_arguments(self):
        ctx = self.context()
        for key in ["password", "targetUrl", "projectPath", "executeShell"]:
            with self.assertRaises(OperationError):
                self.engine.execute(
                    "hmi.inspect", dict(contextHandle=ctx, **{key: "secret"}), "bad-arg"
                )

    def test_hmi_catalog_is_discoverable(self):
        catalog = self.engine.catalog
        self.assertEqual(5, len(catalog.search("", category="hmi")["matches"]))
        self.assertIn("context.open_hmi", catalog.get("hmi.publish")["prerequisites"][0])
        self.assertTrue(catalog.get("hmi.build")["requiresGrant"])
        self.assertFalse(catalog.get("hmi.preflight")["requiresGrant"])


class HmiBackendTests(unittest.TestCase):
    def test_hmi_context_is_forwarded_without_plc_target(self):
        with patch(
            "twincat_mcp.backend.run_shell_step", return_value=({"Success": True}, [])
        ) as run:
            Backend().run(
                Catalog().get("hmi.inspect"),
                {"contextHandle": "ctx"},
                {
                    "kind": "hmi",
                    "solutionPath": "Panel.sln",
                    "projectPath": "Panel.hmiproj",
                    "targetUrl": "https://host/",
                },
            )
            self.assertEqual("hmi-inspect", run.call_args.args[0])
            self.assertEqual(
                {"projectPath": "Panel.hmiproj", "targetUrl": "https://host/"},
                run.call_args.args[1],
            )
            self.assertTrue(run.call_args.kwargs["hmi_only"])
            self.assertNotIn("amsNetId", run.call_args.args[1])
