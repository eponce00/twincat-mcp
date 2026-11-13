"""Structured adapters to the existing TwinCAT automation worker."""

import json
import tempfile
import uuid
from pathlib import Path

from .cli import run_tc_automation, run_tc_automation_with_progress
from .dispatch import run_shell_step
from .hmi import project_path, target_url
from .host import get_shell_host_if_alive, park_shell_host, shutdown_ads_host, shutdown_shell_host
from .routes import run_route_action
from .scope import ScopeSession

# Read-only ADS commands the ADS-only host serves (StepDispatcher.ReadOnlyAdsCommands).
READ_ONLY_ADS = {
    "get-state",
    "read-var",
    "read-var-list",
    "ping-target",
    "list-symbols",
    "read-plc-log",
}


def normalize(value):
    # Normalize protocol property names only; preserve case-sensitive symbol keys
    # inside maps such as Values/Variables.
    if isinstance(value, list):
        return [normalize(v) for v in value]
    if not isinstance(value, dict):
        return value
    return {
        k[0].lower() + k[1:] if k else k: (
            {name: normalize(item) for name, item in v.items()}
            if k.lower() == "results" and isinstance(v, dict)
            else v
            if k.lower() in {"values", "variables"}
            else normalize(v)
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
        # Keep the shell warm for the next context, with its solution closed (see park_shell_host).
        return park_shell_host()

    def close(self):
        self.scope.close()
        shutdown_shell_host()
        shutdown_ads_host()

    def run(self, entry, arguments, context=None):
        args = dict(arguments)
        hmi_publish = args.pop("hmiPublish", None) if entry["id"] == "workflow.deploy" else None
        for key in ("contextHandle", "grantHandle", "confirm", "recordingHandle"):
            if key == "confirm" and entry["id"] in {
                "engineering.delete_object",
                "engineering.move_object",
            }:
                continue
            args.pop(key, None)
        if context and context.get("kind") == "hmi":
            args["projectPath"] = context["projectPath"]
            args["targetUrl"] = context["targetUrl"]
        elif context:
            args["amsNetId"] = context["amsNetId"]
            args["plcName"] = context["plcName"]
        id = entry["id"]
        if id == "system.routes":
            return run_route_action("Get", args)
        if id == "system.route_upsert":
            return run_route_action("Upsert", args)
        if id == "system.route_remove":
            return run_route_action("Remove", args)
        if id == "system.reap_orphans":
            return normalize(run_tc_automation("reap-orphans", []))
        if id.startswith("scope."):
            return self.scope_run(id, args)
        if id == "runtime.record":
            return self.record(args)
        if id == "runtime.write_var_list":
            args["variables"] = json.dumps(args["variables"])
        if id == "engineering.set_variant":
            args["getOnly"] = not bool(args.get("variantName"))
        if id == "engineering.list_variants":
            args["list"] = True
        if id == "engineering.configure_task" and "autostart" in args:
            args["autoStart"] = args.pop("autostart")
        timeout = max(1, int(args.get("timeoutMinutes", 20 if id == "workflow.deploy" else 10)))
        if entry["command"] in {"build", "move-plc-object"} or id.startswith("hmi."):
            timeout = max(timeout, (args.get("timeoutSeconds", 180) + 119) // 60)
        if id.startswith("hmi."):
            timeout = (args.get("timeoutSeconds", 180) + 59) // 60
        result, progress = run_shell_step(
            entry["command"],
            args,
            solution_path=context["solutionPath"] if context else None,
            tc_version=context.get("tcVersion") if context else None,
            timeout_minutes=timeout,
            **({"hmi_only": True} if id.startswith("hmi.") else {}),
            **(
                {"ads_only": True}
                if entry.get("readOnly")
                and not entry.get("engineering")
                and entry["command"] in READ_ONLY_ADS
                else {}
            ),
        )
        result = normalize(result)
        # CLI info and other plain metadata results have no Success property.
        result.setdefault("success", not bool(result.get("errorMessage") or result.get("error")))
        if id in {"runtime.read_var_list", "runtime.write_var_list"} and result.get(
            "errorCount", 0
        ):
            result["success"] = False
        if progress:
            result["progress"] = progress[-50:]
        if hmi_publish is not None:
            result = self.publish_after_deploy(result, args, hmi_publish, context)
        return result

    def publish_after_deploy(self, result, args, spec, context):
        """The HMI publish of workflow.deploy: only after the target is verified in Run, once, never retried."""
        plan = dict(
            projectPath=project_path(spec["projectPath"]),
            targetUrl=target_url(spec["targetUrl"]),
            profileName=spec["profileName"],
        )
        if args.get("dryRun"):
            result["hmiPublish"] = dict(plan, planned=True)
            return result
        if not result.get("success"):
            result["hmiPublish"] = dict(
                plan,
                published=False,
                reason="Not published: the PLC deployment did not end in Run.",
            )
            return result
        step = dict(plan, expectedFingerprint=spec["expectedFingerprint"])
        step["timeoutSeconds"] = spec.get("timeoutSeconds", 180)
        if "credentialTarget" in spec:
            step["credentialTarget"] = spec["credentialTarget"]
        published, progress = run_shell_step(
            "hmi-publish",
            step,
            solution_path=context["solutionPath"] if context else None,
            tc_version=context.get("tcVersion") if context else None,
            timeout_minutes=(step["timeoutSeconds"] + 59) // 60,
            hmi_only=True,
        )
        published = normalize(published)
        published.setdefault(
            "success", not bool(published.get("errorMessage") or published.get("error"))
        )
        if progress:
            published["progress"] = progress[-20:]
        result["hmiPublish"] = published
        if published.get("outcomeUnknown"):
            result["outcomeUnknown"] = True
        if not published["success"]:
            result["success"] = False
            result["errorMessage"] = (
                "The PLC deployment reached Run, but the HMI was not published: "
                + str(published.get("errorMessage") or published.get("error") or "no reason given")
            )
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
        cli = ["--variables", json.dumps(args["variables"]), "--output", output]
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
            cli = ["--variables", json.dumps(args["variables"]), "--output", output]
            for key, flag in flags.items():
                if key in args:
                    cli += [flag, str(args[key])]
            return normalize(run_tc_automation("scope-create", cli))
        cli = ["--input", args["inputPath"], "--format", args.get("format", "csv")]
        if args.get("outputPath"):
            cli += ["--output", args["outputPath"]]
        return normalize(run_tc_automation("scope-export", cli))
