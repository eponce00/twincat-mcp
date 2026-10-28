import os
import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from twincat_mcp.catalog import Catalog
from twincat_mcp.engine import Engine
from twincat_mcp.errors import OperationError
from twincat_mcp.jobs import Jobs
from twincat_mcp.lifecycle import ParentWatchdog, resolve_windows_client


class WaitTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.release = threading.Event()
        self.entered = threading.Event()

        def run(*args):
            self.entered.set()
            self.release.wait(3)
            return {"success": True}

        self.jobs = Jobs(Path(self.directory.name) / "jobs.sqlite3", run)

    def tearDown(self):
        self.release.set()
        self.jobs.close()
        self.directory.cleanup()

    def test_wait_timeout_does_not_resubmit_or_cancel(self):
        job = self.jobs.submit("work", {}, "wait-timeout")
        self.assertTrue(self.entered.wait(1))
        receipt = self.jobs.wait(job["jobHandle"], 0.02)
        self.assertTrue(receipt["waitTimedOut"])
        self.assertEqual("running", receipt["state"])
        self.assertFalse(receipt["cancelRequested"])
        self.assertEqual(0, self.jobs.pending.qsize())

    def test_waiter_cancellation_leaves_job_running(self):
        job = self.jobs.submit("work", {}, "cancel-waiter")
        self.assertTrue(self.entered.wait(1))
        stop = threading.Event()
        result = []
        waiter = threading.Thread(
            target=lambda: result.append(self.jobs.wait(job["jobHandle"], 120, stop))
        )
        waiter.start()
        stop.set()
        waiter.join(1)
        self.assertFalse(waiter.is_alive())
        self.assertEqual("running", result[0]["state"])
        self.assertFalse(result[0]["cancelRequested"])

    def test_completed_job_returns_immediately(self):
        job = self.jobs.submit("work", {}, "finished-wait")
        self.release.set()
        self.jobs.pending.join()
        start = time.monotonic()
        receipt = self.jobs.wait(job["jobHandle"], 120)
        self.assertEqual("succeeded", receipt["state"])
        self.assertFalse(receipt["waitTimedOut"])
        self.assertLess(time.monotonic() - start, 0.1)


class WatchdogTests(unittest.TestCase):
    def test_live_client_is_not_killed_for_being_idle(self):
        parent = Mock(pid=123)
        parent.alive.return_value = True
        lost = Mock()
        exiting = Mock()
        watch = ParentWatchdog(lost, 0.01, parent, exiting)
        with patch.dict(os.environ, {"TWINCAT_MCP_PARENT_WATCHDOG": "1"}):
            watch.start()
            time.sleep(0.25)
            watch.close()
        lost.assert_not_called()
        exiting.assert_not_called()
        parent.close.assert_called_once()

    def test_dead_client_signals_shutdown_and_only_exits_self(self):
        parent = Mock(pid=123)
        parent.alive.return_value = False
        lost = threading.Event()
        exiting = threading.Event()
        watch = ParentWatchdog(lost.set, 0.01, parent, lambda code: exiting.set())
        with patch.dict(os.environ, {"TWINCAT_MCP_PARENT_WATCHDOG": "1"}):
            watch.start()
            self.assertTrue(lost.wait(1))
            self.assertTrue(exiting.wait(1))
            watch.close()
        parent.close.assert_called_once()

    def test_unverified_client_is_not_treated_as_dead(self):
        parent = Mock(pid=123)
        parent.alive.side_effect = OSError("access denied")
        lost = Mock()
        watch = ParentWatchdog(lost, 0.01, parent, Mock())
        with patch.dict(os.environ, {"TWINCAT_MCP_PARENT_WATCHDOG": "1"}):
            watch.start()
            time.sleep(0.25)
            watch.close()
        lost.assert_not_called()

    def test_windows_venv_redirector_resolves_actual_client(self):
        direct = Mock(pid=11)
        direct.image.return_value = os.path.normcase(os.path.abspath("venv/python.exe"))
        direct.parent_pid.return_value = 10
        direct.created.return_value = 20
        own = Mock(pid=12)
        own.image.return_value = os.path.normcase(os.path.abspath("base/python.exe"))
        client = Mock(pid=10)
        client.created.return_value = 10
        with (
            patch("twincat_mcp.lifecycle.os.getppid", return_value=11),
            patch("twincat_mcp.lifecycle.os.getpid", return_value=12),
            patch.object(sys, "executable", "venv/python.exe"),
            patch.object(sys, "prefix", "venv"),
            patch.object(sys, "base_prefix", "base"),
        ):
            result = resolve_windows_client(lambda pid: {11: direct, 12: own, 10: client}[pid])
        self.assertIs(client, result)
        direct.close.assert_called_once()
        own.close.assert_called_once()
        client.close.assert_not_called()


class ContractTests(unittest.TestCase):
    def test_verifying_build_requires_expectations(self):
        catalog = Catalog()
        with self.assertRaises(OperationError):
            catalog.validate("workflow.build_verify", {"contextHandle": "context"})
        for key in ("expectedSymbols", "expectedTypes"):
            catalog.validate("workflow.build_verify", {"contextHandle": "context", key: ["MAIN.*"]})

    def test_long_wait_and_symbol_bounds(self):
        catalog = Catalog()
        catalog.validate("job.wait", {"jobHandle": "job", "waitSeconds": 120})
        with self.assertRaises(OperationError):
            catalog.validate("job.wait", {"jobHandle": "job", "waitSeconds": 121})
        with self.assertRaises(OperationError):
            catalog.validate("engineering.symbols", {"contextHandle": "context", "limit": 201})

    def test_linking_only_update_is_explicit(self):
        Catalog().validate(
            "engineering.update_object",
            dict(
                contextHandle="ctx",
                grantHandle="grant",
                path="TIPC^PLC^PLC Project^GVLs^GVL",
                expectedSha256="0" * 64,
                linkingPolicy="always",
            ),
        )

    def test_wait_needs_no_request_key(self):
        with tempfile.TemporaryDirectory() as directory:
            engine = Engine(directory, Mock())
            try:
                with self.assertRaisesRegex(OperationError, "Unknown job"):
                    engine.execute("job.wait", {"jobHandle": "missing", "waitSeconds": 0})
            finally:
                engine.close()
