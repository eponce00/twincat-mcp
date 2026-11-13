"""Speed without carried state: read-only ADS work has its own lane and its own ADS-only host, so it never
waits behind an engineering job; a closed context's shell stays warm with its solution closed, so the next
context opens its solution from disk and nothing of the previous one (platform, variant) carries over."""

import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from twincat_mcp import host as host_module
from twincat_mcp.backend import Backend
from twincat_mcp.catalog import Catalog
from twincat_mcp.engine import Engine
from twincat_mcp.host import HostError, ShellHost

TARGET = "192.168.1.108.1.1"
ROOT = Path(__file__).resolve().parents[2]


class SlowBackend:
    """Engineering work blocks until released; everything else answers at once."""

    def __init__(self):
        self.release = threading.Event()
        self.building = threading.Event()
        self.order = []

    def run(self, entry, arguments, context=None):
        self.order.append(entry["id"])
        if entry["engineering"]:
            self.building.set()
            self.release.wait(5)
        return {"success": True}

    def alive(self):
        return True

    def started(self):
        return True

    def close_context(self):
        return "parked"

    def close(self):
        pass


class LaneTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.backend = SlowBackend()
        self.engine = Engine(state_dir=self.folder.name, backend=self.backend)
        self.solution = Path(self.folder.name) / "Test.sln"
        self.solution.write_text("")

    def tearDown(self):
        self.backend.release.set()
        self.engine.close()
        self.folder.cleanup()

    def wait(self, receipt, seconds=3):
        end = time.monotonic() + seconds
        while receipt["state"] not in {"succeeded", "failed", "cancelled", "outcome_unknown"}:
            self.assertLess(time.monotonic(), end, receipt)
            time.sleep(0.01)
            receipt = self.engine.jobs.get(receipt["jobHandle"])
        return receipt

    def test_lanes_follow_the_catalog(self):
        lanes = {op: self.engine.lane(op) for op in Catalog().entries}
        for op in (
            "runtime.ping_target",
            "runtime.get_state",
            "runtime.read_var",
            "runtime.read_var_list",
            "runtime.list_symbols",
            "runtime.read_plc_log",
            "system.routes",
        ):
            self.assertEqual("read", lanes[op], op)
        for op in (
            "engineering.build",
            "engineering.set_variant",
            "runtime.write_var",
            "workflow.deploy",
            "workflow.wait_state",
            "context.open",
            "context.close",
            "scope.status",
        ):
            self.assertEqual("serial", lanes[op], op)

    def test_reads_answer_while_an_engineering_job_runs(self):
        opened = self.wait(
            self.engine.execute(
                "context.open",
                dict(solutionPath=str(self.solution), amsNetId=TARGET, plcName="PLC1"),
                "open",
            )
        )
        handle = self.engine.jobs.result(opened["jobHandle"], 0, 4000)["text"]
        handle = __import__("json").loads(handle)["contextHandle"]
        build = self.engine.execute("engineering.build", dict(contextHandle=handle), "build")
        self.assertTrue(self.backend.building.wait(2))
        start = time.monotonic()
        ping = self.wait(self.engine.execute("runtime.ping_target", dict(amsNetId=TARGET), "ping"))
        self.assertEqual("succeeded", ping["state"])
        self.assertLess(time.monotonic() - start, 1.0, "The ping did not wait for the build")
        self.assertEqual("running", self.engine.jobs.get(build["jobHandle"])["state"])
        self.backend.release.set()
        self.assertEqual("succeeded", self.wait(build)["state"])


class AdsHostRoutingTests(unittest.TestCase):
    def test_read_only_ads_commands_use_the_ads_only_host(self):
        backend = Backend()
        catalog = Catalog()
        calls = []

        def fake(command, args, solution_path=None, tc_version=None, timeout_minutes=10, **flags):
            calls.append((command, flags))
            return {"success": True}, []

        with patch("twincat_mcp.backend.run_shell_step", side_effect=fake):
            backend.run(catalog.get("runtime.ping_target"), {"amsNetId": TARGET})
            backend.run(catalog.get("runtime.read_var"), {"amsNetId": TARGET, "symbol": "MAIN.x"})
            backend.run(
                catalog.get("runtime.write_var"),
                {"amsNetId": TARGET, "symbol": "MAIN.x", "value": "1", "grantHandle": "g"},
            )
        self.assertEqual(("ping-target", {"ads_only": True}), calls[0])
        self.assertEqual(("read-var", {"ads_only": True}), calls[1])
        self.assertEqual(("write-var", {}), calls[2], "Writes stay on the shell host")

    def test_ads_only_host_never_opens_a_solution(self):
        ads = ShellHost(Path("unused.exe"), ads_only=True)
        with (
            patch.object(ads, "ensure_solution") as ensure,
            patch.object(ads, "is_alive", return_value=True),
            patch.object(ads, "_call_raw_locked", return_value={"result": {"success": True}}),
        ):
            ads.execute_step("build", {}, "sample.sln", None)
        ensure.assert_not_called()
        with self.assertRaises(HostError):
            ads.release_solution()

    def test_native_ads_only_host_refuses_writes_and_shell_methods(self):
        source = (ROOT / "TcAutomation" / "Commands" / "HostCommand.cs").read_text(
            encoding="utf-8-sig"
        )
        self.assertIn(
            "if (_adsOnly && !StepDispatcher.ReadOnlyAdsCommands.Contains(command!))", source
        )
        self.assertIn(
            'if (_adsOnly && (method == "ensure-solution" || method == "release-solution"))', source
        )
        self.assertIn("if (!adsOnly)", source, "No session file or reaping for the ADS-only host")
        dispatcher = (ROOT / "TcAutomation" / "Core" / "StepDispatcher.cs").read_text(
            encoding="utf-8-sig"
        )
        read_only = dispatcher[dispatcher.index("ReadOnlyAdsCommands") :]
        read_only = read_only[: read_only.index("};")]
        for write in ("set-state", "write-var", "write-var-list"):
            self.assertNotIn(f'"{write}"', read_only)


class WarmShellTests(unittest.TestCase):
    def tearDown(self):
        host_module._shell_host = None

    def test_close_parks_the_shell_with_its_solution_closed(self):
        shell = ShellHost(Path("unused.exe"))
        shell._current_solution = "A.sln"
        shell._current_tc_version = "3.1.4026.26"
        host_module._shell_host = shell
        with (
            patch.object(shell, "is_alive", return_value=True),
            patch.object(
                shell, "_call_raw_locked", return_value={"released": True, "loaded": False}
            ) as call,
            patch.dict("os.environ", {"TWINCAT_SHELL_WARM_SECONDS": "60"}),
        ):
            self.assertEqual("parked", host_module.park_shell_host())
        call.assert_called_once_with("release-solution", None, timeout=120.0)
        self.assertIsNone(shell._current_solution, "The next context must open its solution again")
        self.assertIsNotNone(shell.parked_since)
        self.assertIs(
            host_module._shell_host, shell, "The shell process stays for the next context"
        )

    def test_a_shell_that_cannot_release_is_shut_down(self):
        shell = ShellHost(Path("unused.exe"))
        host_module._shell_host = shell
        with (
            patch.object(shell, "is_alive", return_value=True),
            patch.object(
                shell, "_call_raw_locked", return_value={"released": True, "loaded": True}
            ),
            patch.object(shell, "shutdown") as shutdown,
            patch.dict("os.environ", {"TWINCAT_SHELL_WARM_SECONDS": "60"}),
        ):
            self.assertEqual("stopped", host_module.park_shell_host())
        shutdown.assert_called_once()
        self.assertIsNone(host_module._shell_host)

    def test_warm_zero_keeps_the_old_shutdown(self):
        shell = ShellHost(Path("unused.exe"))
        host_module._shell_host = shell
        with (
            patch.object(shell, "is_alive", return_value=True),
            patch.object(shell, "shutdown") as shutdown,
            patch.dict("os.environ", {"TWINCAT_SHELL_WARM_SECONDS": "0"}),
        ):
            self.assertEqual("stopped", host_module.park_shell_host())
        shutdown.assert_called_once()

    def test_idle_parked_shell_expires(self):
        shell = ShellHost(Path("unused.exe"))
        host_module._shell_host = shell
        with (
            patch.object(shell, "is_alive", return_value=True),
            patch.object(
                shell, "_call_raw_locked", return_value={"released": True, "loaded": False}
            ),
            patch.object(shell, "shutdown") as shutdown,
            patch.dict("os.environ", {"TWINCAT_SHELL_WARM_SECONDS": "0.2"}),
        ):
            self.assertEqual("parked", host_module.park_shell_host())
            time.sleep(0.5)
        shutdown.assert_called_once()

    def test_native_reopen_reloads_a_released_solution(self):
        source = (ROOT / "TcAutomation" / "Commands" / "HostCommand.cs").read_text(
            encoding="utf-8-sig"
        )
        self.assertIn("else if (!_vsInstance.IsSolutionLoaded", source)
        vs = (ROOT / "TcAutomation" / "Core" / "VisualStudioInstance.cs").read_text(
            encoding="utf-8-sig"
        )
        self.assertIn("_solution.Close(false);", vs[vs.index("public bool CloseSolution()") :])


if __name__ == "__main__":
    unittest.main()
