"""Real stdio tests; no TwinCAT executable or PLC is used."""

import json
import os
import queue
import subprocess
import sys
import tempfile
import threading
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
META = {
    "io.modelcontextprotocol/protocolVersion": "2026-07-28",
    "io.modelcontextprotocol/clientCapabilities": {},
    "io.modelcontextprotocol/clientInfo": {"name": "wire-tests", "version": "1"},
}


class WireTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.folder = tempfile.TemporaryDirectory()
        cls.proc = subprocess.Popen(
            [sys.executable, str(ROOT / "server.py")],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            env={
                **os.environ,
                "TWINCAT_DISABLE_HOST": "1",
                "TWINCAT_MCP_STATE_DIR": cls.folder.name,
                "TWINCAT_AUTOMATION_EXE": str(Path(cls.folder.name) / "does-not-exist.exe"),
            },
        )
        cls.lines = queue.Queue()
        cls.stderr = []

        def output():
            for line in cls.proc.stdout:
                cls.lines.put(line)

        def errors():
            for line in cls.proc.stderr:
                cls.stderr.append(line)

        cls.out_thread = threading.Thread(target=output, daemon=True)
        cls.err_thread = threading.Thread(target=errors, daemon=True)
        cls.out_thread.start()
        cls.err_thread.start()
        cls.id = 0

    @classmethod
    def tearDownClass(cls):
        cls.proc.stdin.close()
        try:
            cls.proc.wait(timeout=8)
        except subprocess.TimeoutExpired:
            cls.proc.kill()
            cls.proc.wait()
            raise AssertionError("Server did not exit on EOF") from None
        finally:
            cls.out_thread.join(2)
            cls.err_thread.join(2)
            cls.proc.stdout.close()
            cls.proc.stderr.close()
            cls.folder.cleanup()
        if cls.proc.returncode != 0:
            raise AssertionError("".join(cls.stderr))

    def request(self, method, params=None, meta=None):
        type(self).id += 1
        id = type(self).id
        body = dict(params or {})
        body["_meta"] = META if meta is None else meta
        message = dict(jsonrpc="2.0", id=id, method=method, params=body)
        self.proc.stdin.write(json.dumps(message) + "\n")
        self.proc.stdin.flush()
        while True:
            # Any non-JSON stdout line is a protocol failure.
            response = json.loads(self.lines.get(timeout=8))
            if response.get("id") == id:
                return response

    def call(self, name, args):
        return self.request("tools/call", dict(name=name, arguments=args))

    def test_discover_without_initialization(self):
        r = self.request("server/discover")["result"]
        self.assertEqual("complete", r["resultType"])
        self.assertIn("2026-07-28", r["supportedVersions"])
        self.assertIn("ttlMs", r)
        self.assertIn("cacheScope", r)
        self.assertEqual("2.0.0", r["_meta"]["io.modelcontextprotocol/serverInfo"]["version"])

    def test_modern_list_has_three_fixed_tools_and_cache_hints(self):
        r = self.request("tools/list")["result"]
        self.assertEqual(
            ["twincat_search", "twincat_describe", "twincat_execute"],
            [t["name"] for t in r["tools"]],
        )
        self.assertEqual("complete", r["resultType"])
        self.assertEqual("public", r["cacheScope"])
        self.assertGreater(r["ttlMs"], 0)
        for t in r["tools"]:
            self.assertIn("outputSchema", t)

    def test_search_then_describe_does_not_grow_tool_list(self):
        before = self.request("tools/list")["result"]["tools"]
        r = self.call("twincat_search", dict(query="build"))
        self.assertFalse(r["result"]["isError"])
        matches = r["result"]["structuredContent"]["matches"]
        id = matches[0]["operation"]
        detail = self.call("twincat_describe", dict(operations=[id]))
        self.assertIn("inputSchema", detail["result"]["structuredContent"]["operations"][0])
        self.assertEqual(before, self.request("tools/list")["result"]["tools"])

    def test_unknown_tool_is_protocol_error(self):
        self.assertEqual(-32602, self.call("missing_tool", {})["error"]["code"])

    def test_unknown_operation_is_error_flagged(self):
        r = self.call("twincat_execute", dict(operation="missing.operation", arguments={}))
        self.assertTrue(r["result"]["isError"])
        self.assertEqual("unknown_operation", r["result"]["structuredContent"]["code"])

    def test_missing_and_extra_arguments_are_error_flagged(self):
        for args in [
            dict(operation="runtime.get_state", arguments={}),
            dict(operation="system.status", arguments={"extra": True}),
        ]:
            self.assertTrue(self.call("twincat_execute", args)["result"]["isError"])

    def test_unauthorized_mutation_has_failed_receipt(self):
        r = self.call(
            "twincat_execute",
            dict(
                operation="runtime.write_var",
                arguments=dict(
                    amsNetId="1.2.3.4.1.1", symbol="MAIN.x", value="1", grantHandle="missing"
                ),
                requestKey="wire-denied-write",
            ),
        )["result"]
        self.assertTrue(r["isError"])
        receipt = r["structuredContent"]
        self.assertEqual("failed", receipt["state"])
        self.assertEqual("grant_required", receipt["result"]["code"])

    def test_search_limit_is_enforced(self):
        r = self.call("twincat_search", dict(limit=100))
        self.assertTrue(r["result"]["isError"])

    def test_unsupported_modern_version_rejected(self):
        meta = dict(META)
        meta["io.modelcontextprotocol/protocolVersion"] = "2099-01-01"
        r = self.request("tools/list", meta=meta)
        self.assertEqual(-32022, r["error"]["code"])

    def test_read_only_status_does_not_need_request_key(self):
        r = self.call("twincat_execute", dict(operation="system.status", arguments={}))["result"]
        self.assertFalse(r["isError"])
        self.assertEqual("2.0.0", r["structuredContent"]["version"])
