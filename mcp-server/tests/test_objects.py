import sys
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from twincat_mcp.backend import Backend
from twincat_mcp.catalog import Catalog
from twincat_mcp.errors import OperationError
from twincat_mcp.host import ShellHost


class ObjectCatalogTests(unittest.TestCase):
    def setUp(self):
        self.catalog = Catalog()
        self.base = dict(contextHandle="context", grantHandle="grant")

    def validate(self, operation, **arguments):
        return self.catalog.validate("engineering." + operation, dict(self.base, **arguments))

    def test_mutations_and_exports_require_context_scoped_grants(self):
        for operation in (
            "create_folder",
            "create_object",
            "update_object",
            "rename_object",
            "delete_object",
            "import_objects",
            "export_objects",
        ):
            entry = self.catalog.get("engineering." + operation)
            self.assertTrue(entry["engineering"])
            self.assertTrue(entry["requiresGrant"])
            self.assertFalse(entry["readOnly"])
            self.assertIn("grantHandle", entry["inputSchema"]["required"])
            self.assertIn("contextHandle", entry["inputSchema"]["required"])

    def test_creation_requires_type_specific_fields(self):
        for kind in ("function", "property", "interfaceMethod", "interfaceProperty"):
            with self.assertRaises(OperationError):
                self.validate(
                    "create_object",
                    parentPath="PLC^POUs",
                    name="F_Test",
                    objectType=kind,
                    declaration="source",
                )
        with self.assertRaises(OperationError):
            self.validate(
                "create_object", parentPath="PLC^POUs", name="FB_Test", objectType="functionBlock"
            )
        result = self.validate(
            "create_object",
            parentPath="PLC^POUs",
            name="F_Test",
            objectType="function",
            declaration="FUNCTION F_Test : INT",
            returnType="INT",
            implementation="F_Test := 1;",
        )
        self.assertEqual("PUBLIC", result["access"])

    def test_update_needs_content_and_stale_state_precondition(self):
        for arguments in ({}, {"expectedSha256": "0" * 64}, {"implementation": "// ST"}):
            with self.assertRaises(OperationError):
                self.validate("update_object", path="PLC^POUs^FB", **arguments)

    def test_recursive_delete_requires_separate_confirmation(self):
        args = dict(
            path="PLC^POUs^Folder", expectedSha256="0" * 64, confirm="CONFIRM", recursive=True
        )
        with self.assertRaises(OperationError):
            self.validate("delete_object", **args)
        self.validate("delete_object", **args, recursiveConfirm="DELETE_CONTENTS")

    def test_native_import_requires_parent_and_file_hash(self):
        args = dict(format="native", filePath="C:/Exports/FB.TcPOU", expectedFileSha256="0" * 64)
        with self.assertRaises(OperationError):
            self.validate("import_objects", **args)
        self.validate("import_objects", **args, parentPath="PLC^POUs")
        with self.assertRaises(OperationError):
            self.validate(
                "import_objects",
                format="native",
                filePath="C:/Exports/FB.TcPOU",
                parentPath="PLC^POUs",
            )

    def test_export_selection_is_bounded(self):
        for paths in ([], ["PLC^POUs^FB"] * 33):
            with self.assertRaises(OperationError):
                self.validate(
                    "export_objects", format="plcopen", filePath="C:/Exports/PLC.xml", paths=paths
                )

    def test_describe_examples_validate_for_all_object_operations(self):
        for operation in self.catalog.entries:
            if operation.endswith(("_object", "_objects", "_folder")):
                entry = self.catalog.get(operation)
                self.catalog.validate(operation, entry["example"]["arguments"])

    def test_host_loads_the_solution_for_new_commands(self):
        commands = [
            e["command"]
            for e in self.catalog.entries.values()
            if e["id"].endswith(("_object", "_objects", "_folder"))
        ]
        for command in commands:
            host = ShellHost(Path("unused.exe"))
            with (
                patch.object(host, "ensure_solution") as ensure,
                patch.object(host, "is_alive", return_value=True),
                patch.object(host, "_call_raw_locked", return_value={"result": {"success": True}}),
            ):
                host.execute_step(command, {}, "fixture.sln", None)
                ensure.assert_called_once_with("fixture.sln", None, timeout=120.0)

    def test_adapter_injects_context_and_preserves_partial_receipts(self):
        receipt = dict(
            success=False,
            mutationAttempted=True,
            outcomeUnknown=True,
            createdPath="PLC^Objects^FB",
            saved=False,
            errorMessage="save failed",
        )
        with patch("twincat_mcp.backend.run_shell_step", return_value=(receipt, [])) as run:
            result = Backend().run(
                self.catalog.get("engineering.create_object"),
                dict(
                    self.base,
                    parentPath="PLC^Objects",
                    name="FB",
                    objectType="functionBlock",
                    declaration="FUNCTION_BLOCK FB",
                ),
                dict(solutionPath="fixture.sln", amsNetId="1.2.3.4.1.1", plcName="PLC"),
            )
        self.assertEqual(receipt, result)
        self.assertEqual("PLC", run.call_args.args[1]["plcName"])
        self.assertEqual("1.2.3.4.1.1", run.call_args.args[1]["amsNetId"])
        self.assertNotIn("grantHandle", run.call_args.args[1])

    def test_delete_confirmation_reaches_the_native_guard(self):
        with patch(
            "twincat_mcp.backend.run_shell_step", return_value=({"success": True}, [])
        ) as run:
            Backend().run(
                self.catalog.get("engineering.delete_object"),
                dict(
                    self.base,
                    path="PLC^Motion",
                    expectedSha256="0" * 64,
                    confirm="CONFIRM",
                    recursive=True,
                    recursiveConfirm="DELETE_CONTENTS",
                ),
                dict(solutionPath="fixture.sln", amsNetId="1.2.3.4.1.1", plcName="PLC"),
            )
        self.assertEqual("CONFIRM", run.call_args.args[1]["confirm"])
        self.assertEqual("DELETE_CONTENTS", run.call_args.args[1]["recursiveConfirm"])
