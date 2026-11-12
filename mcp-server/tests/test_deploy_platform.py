"""workflow.deploy build selection and engineering.list_variants: catalog contracts and what reaches the native step.
No TwinCAT, XAE or PLC is used; the native step is mocked."""

import sys
import unittest
from pathlib import Path
from unittest.mock import patch

from jsonschema import Draft202012Validator

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from twincat_mcp.backend import Backend
from twincat_mcp.catalog import Catalog

CTX = dict(solutionPath="sample.sln", amsNetId="192.168.1.108.1.1", plcName="PLC1")


class DeployPlatformContract(unittest.TestCase):
    def test_deploy_takes_variant_configuration_platform_and_boot_choice(self):
        schema = Catalog().get("workflow.deploy")["inputSchema"]
        for name in (
            "variant",
            "configuration",
            "platform",
            "generateBootProject",
            "allowUnverifiedPlatform",
        ):
            self.assertIn(name, schema["properties"])
        self.assertTrue(schema["properties"]["generateBootProject"]["default"])
        self.assertFalse(schema["properties"]["allowUnverifiedPlatform"]["default"])
        validator = Draft202012Validator(schema)
        base = dict(contextHandle="c", grantHandle="g", confirm="CONFIRM")
        self.assertTrue(validator.is_valid({**base, "variant": "Station", "dryRun": True}))
        self.assertFalse(
            validator.is_valid({**base, "platform": ""}), "empty platform is not a choice"
        )
        self.assertFalse(
            validator.is_valid({**base, "amsNetId": "1.2.3.4.1.1"}),
            "the target comes from the context only",
        )

    def test_deploy_arguments_reach_the_native_step_with_the_context_target(self):
        args = dict(
            variant="LocalHil", platform="TwinCAT OS (x64)", generateBootProject=False, dryRun=True
        )
        with patch(
            "twincat_mcp.backend.run_shell_step", return_value=({"Success": True}, [])
        ) as run:
            Backend().run(Catalog().get("workflow.deploy"), dict(args), CTX)
        command, sent = run.call_args.args[0], run.call_args.args[1]
        self.assertEqual("deploy", command)
        self.assertEqual("192.168.1.108.1.1", sent["amsNetId"])
        for key, value in args.items():
            self.assertEqual(value, sent[key])

    def test_refused_deploy_is_a_failed_receipt(self):
        refusal = {
            "Success": False,
            "ErrorMessage": "Build platform 'TwinCAT RT (x64)' (arguments) does not match target 192.168.1.108.1.1",
            "Platform": "TwinCAT RT (x64)",
            "PlatformVerified": False,
        }
        with patch("twincat_mcp.backend.run_shell_step", return_value=(refusal, [])):
            result = Backend().run(
                Catalog().get("workflow.deploy"), dict(platform="TwinCAT RT (x64)"), CTX
            )
        self.assertFalse(result["success"])
        self.assertIn("does not match", result["errorMessage"])

    def test_list_variants_is_read_only_and_needs_no_grant(self):
        entry = Catalog().get("engineering.list_variants")
        self.assertTrue(entry["readOnly"])
        self.assertFalse(entry["requiresGrant"])
        self.assertEqual("set-variant", entry["command"])
        self.assertFalse(entry["inputSchema"]["properties"]["probeTargets"]["default"])
        with patch(
            "twincat_mcp.backend.run_shell_step", return_value=({"Success": True}, [])
        ) as run:
            Backend().run(entry, dict(order=["LocalHil", "Station"]), CTX)
        sent = run.call_args.args[1]
        self.assertTrue(sent["list"])
        self.assertNotIn("getOnly", sent, "listing never takes the set path")
        self.assertEqual(["LocalHil", "Station"], sent["order"])

    def test_set_variant_still_queries_without_a_name(self):
        with patch(
            "twincat_mcp.backend.run_shell_step", return_value=({"Success": True}, [])
        ) as run:
            Backend().run(Catalog().get("engineering.set_variant"), {}, CTX)
        self.assertTrue(run.call_args.args[1]["getOnly"])
        self.assertNotIn("list", run.call_args.args[1])


if __name__ == "__main__":
    unittest.main()
