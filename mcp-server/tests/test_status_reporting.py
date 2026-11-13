"""Status reporting: route listings say whether "online" is a live probe or the router's cached flag, and the
ping catalog entry names the stable classifications (a target stopped in Config is not "rebooting")."""

import json
import sys
import unittest
from pathlib import Path
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from twincat_mcp.catalog import Catalog
from twincat_mcp.errors import OperationError
from twincat_mcp.routes import run_route_action

ROOT = Path(__file__).resolve().parents[2]


class RouteStatusTests(unittest.TestCase):
    def run_get(self, arguments):
        payloads = []

        def fake_run(command, **_kwargs):
            payload_path = command[command.index("-PayloadPath") + 1]
            payloads.append(json.loads(Path(payload_path).read_text(encoding="utf-8")))
            receipt = {"success": True, "probed": True, "routes": []}
            return Mock(returncode=0, stdout=json.dumps(receipt), stderr="")

        with (
            patch("twincat_mcp.routes._powershell_executable", return_value="powershell.exe"),
            patch("twincat_mcp.routes.subprocess.run", side_effect=fake_run),
        ):
            result = run_route_action("Get", arguments)
        return result, payloads[0]

    def test_probe_options_reach_the_script(self):
        _, payload = self.run_get({"probe": False, "timeoutMs": 800})
        self.assertEqual(payload, {"probe": False, "timeoutMs": 800})
        _, payload = self.run_get({})
        self.assertEqual(payload, {}, "Defaults are the script's: probe on, 1500 ms")

    def test_script_labels_online_as_probe_or_router_cache(self):
        script = (ROOT / "mcp-server" / "twincat_mcp" / "ads_routes.ps1").read_text(
            encoding="utf-8"
        )
        self.assertIn('onlineSource = if ($Probed) { "probe" } else { "routerCache" }', script)
        self.assertIn("onlineCached = $cached", script)
        self.assertIn("Get-AdsState -NetId $ids -Port 10000 -Timeout $timeoutMs", script)

    def test_catalog_accepts_probe_options_and_nothing_else(self):
        catalog = Catalog()
        catalog.validate("system.routes", {})
        catalog.validate("system.routes", {"probe": False, "timeoutMs": 1500})
        for bad in ({"probe": "yes"}, {"timeoutMs": 50}, {"timeoutMs": 20000}, {"verbose": True}):
            with self.assertRaises(OperationError, msg=bad):
                catalog.validate("system.routes", bad)
        self.assertTrue(catalog.get("system.routes")["readOnly"])


class PingStatusTests(unittest.TestCase):
    def test_catalog_names_the_stable_classifications(self):
        description = Catalog().get("runtime.ping_target")["description"]
        for name in (
            "config",
            "stopped",
            "runtimeUnavailable",
            "rebooting (only while a state is changing)",
        ):
            self.assertIn(name, description)

    def test_native_classifier_reports_changing_states_only_as_rebooting(self):
        path = ROOT / "TcAutomation" / "Commands" / "PingTargetCommand.cs"
        source = path.read_text(encoding="utf-8-sig").replace("\r\n", "\n")
        stable = (
            'private static readonly string[] StableStates = { "Run", "Config", "Stop", "Error" };'
        )
        self.assertIn(stable, source)
        self.assertIn('if (changing)\n                return "rebooting";', source)
        self.assertIn('return "config";', source)
        self.assertIn("result.SystemServiceState = sysProbe.AdsState;", source)


if __name__ == "__main__":
    unittest.main()
