"""Structured adapters to the existing TwinCAT automation worker."""

import json
import os
import tempfile
import uuid
import xml.etree.ElementTree as ET
from pathlib import Path

from .cli import run_tc_automation, run_tc_automation_with_progress
from .dispatch import run_shell_step
from .host import get_shell_host_if_alive, shutdown_shell_host
from .scope import ScopeSession


def normalize(value):
    # Normalize protocol property names only; preserve case-sensitive symbol keys
    # inside maps such as Values/Variables.
    if isinstance(value, list):
        return [normalize(v) for v in value]
    if not isinstance(value, dict):
        return value
    return {
        k[0].lower() + k[1:] if k else k: (
            v if k.lower() in {"values", "variables"} else normalize(v)
        )
        for k, v in value.items()
    }


class Backend:
    def __init__(self):
        self.scope = ScopeSession()

    def alive(self):
        host = get_shell_host_if_alive()
        return host is not None and host.is_alive()

    def started(self):
        host = get_shell_host_if_alive()
        return host is not None and host._proc is not None

    def close_context(self):
        shutdown_shell_host()

    def close(self):
        self.scope.close()
        self.close_context()

    def run(self, entry, arguments, context=None):
        args = dict(arguments)
        for key in ("contextHandle", "grantHandle", "confirm", "recordingHandle"):
            args.pop(key, None)
        if context:
            args["amsNetId"] = context["amsNetId"]
            args["plcName"] = context["plcName"]
        id = entry["id"]
        if id == "system.routes":
            return self.routes()
        if id == "system.reap_orphans":
            return normalize(run_tc_automation("reap-orphans", []))
        if id.startswith("scope."):
            return self.scope_run(id, args)
        if id == "runtime.record":
            return self.record(args)
        if id == "runtime.read_var_list":
            args["symbols"] = ",".join(args["symbols"])
        if id == "runtime.write_var_list":
            args["variables"] = json.dumps(args["variables"])
        if id == "engineering.set_variant":
            args["getOnly"] = not bool(args.get("variantName"))
        if id == "engineering.configure_task" and "autostart" in args:
            args["autoStart"] = args.pop("autostart")
        timeout = max(1, int(args.get("timeoutMinutes", 20 if id == "workflow.deploy" else 10)))
        result, progress = run_shell_step(
            entry["command"],
            args,
            solution_path=context["solutionPath"] if context else None,
            tc_version=context.get("tcVersion") if context else None,
            timeout_minutes=timeout,
        )
        result = normalize(result)
        # CLI info and other plain metadata results have no Success property.
        result.setdefault("success", not bool(result.get("errorMessage") or result.get("error")))
        if progress:
            result["progress"] = progress[-50:]
        return result

    def record(self, args):
        output = args.get("outputPath") or str(
            Path(tempfile.gettempdir()) / ("ads-" + uuid.uuid4().hex + ".csv")
        )
        flags = {
            "amsNetId": "--amsnetid",
            "port": "--port",
            "sampleTimeMs": "--sampletime",
            "durationSec": "--duration",
            "maxTimeSec": "--max-time",
            "startTrigger": "--start-trigger",
            "stopTrigger": "--stop-trigger",
        }
        cli = ["--variables", ",".join(args["variables"]), "--output", output]
        for key, flag in flags.items():
            if key in args:
                cli += [flag, str(args[key])]
        timeout = int((args.get("durationSec", 0) + args.get("maxTimeSec", 60) + 30) / 60) + 1
        result, _ = run_tc_automation_with_progress("ads-record", cli, timeout)
        return normalize(result)

    def scope_run(self, id, args):
        if id == "scope.start":
            self.scope.start()
            return self.scope.send_command({"command": "start", "configPath": args["configPath"]})
        if id == "scope.stop":
            result = self.scope.send_command(
                {
                    "command": "stop",
                    "outputPath": args.get("outputPath", ""),
                    "format": args.get("format", "csv"),
                }
            )
            if result.get("success"):
                self.scope.close()
            return result
        if id == "scope.status":
            return self.scope.send_command({"command": "status"})
        if id == "scope.create_config":
            flags = {
                "amsNetId": "--amsnetid",
                "port": "--port",
                "sampleTimeMs": "--sampletime",
                "recordTimeSec": "--recordtime",
                "chartName": "--chartname",
            }
            output = args.get("outputPath") or str(
                Path(tempfile.gettempdir()) / ("scope-" + uuid.uuid4().hex + ".tcscopex")
            )
            cli = ["--variables", ",".join(args["variables"]), "--output", output]
            for key, flag in flags.items():
                if key in args:
                    cli += [flag, str(args[key])]
            return normalize(run_tc_automation("scope-create", cli))
        cli = ["--input", args["inputPath"], "--format", args.get("format", "csv")]
        if args.get("outputPath"):
            cli += ["--output", args["outputPath"]]
        return normalize(run_tc_automation("scope-export", cli))

    @staticmethod
    def routes():
        roots = [
            Path(os.environ.get("TWINCAT3DIR", "C:/TwinCAT/3.1")),
            Path("C:/TwinCAT/3.1"),
            Path("C:/Program Files/Beckhoff/TwinCAT/3.1"),
        ]
        for root in roots:
            file = root / "Target/StaticRoutes.xml"
            if file.is_file():
                tree = ET.parse(file)
                return {
                    "success": True,
                    "routes": [
                        {
                            "name": r.findtext("Name"),
                            "amsNetId": r.findtext("NetId"),
                            "address": r.findtext("Address"),
                        }
                        for r in tree.findall(".//Route")
                    ],
                }
        return {"success": False, "errorMessage": "TwinCAT StaticRoutes.xml not found."}
