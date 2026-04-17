"""
Batch orchestration handler. Runs multiple shell/ADS steps in a single
TwinCAT DTE session via the C# BatchCommand. Most of the logic here is
input validation + result formatting; the actual execution is delegated
to `TcAutomation.exe batch --input <tmpfile>`.

twincat_batch is NOT in DANGEROUS_TOOLS (batch itself is inert), but
individual step commands can be. We do two batch-aware pre-flight checks
that parallel the per-tool gates in call_tool:
  - armed-mode gate, if any step's command is in DANGEROUS_BATCH_COMMANDS
  - confirmation gate,    if any step's command is in CONFIRMATION_REQUIRED_BATCH_COMMANDS
"""

import json
import os
import tempfile

from mcp.types import TextContent

from ..cli import run_tc_automation_with_progress
from ..formatting import add_timing_to_output, format_duration
from ..safety import (
    ARMED_MODE_TTL,
    CONFIRM_TOKEN,
    CONFIRMATION_REQUIRED_BATCH_COMMANDS,
    DANGEROUS_BATCH_COMMANDS,
    is_armed,
)
from ._registry import register


def _ci_get(d: dict, *names, default=None):
    """Case-insensitive dict get; returns first match among `names`.

    BatchCommand re-serializes shell-step results with
    JsonNamingPolicy.CamelCase, but ADS steps are captured from the
    original stdout which is PascalCase. So we use case-insensitive
    lookups to handle both shapes inside a single batch.
    """
    if not isinstance(d, dict):
        return default
    lowered = {k.lower(): k for k in d.keys()}
    for n in names:
        key = lowered.get(n.lower())
        if key is not None:
            return d[key]
    return default


@register("twincat_batch")
async def handle_batch(arguments: dict, tool_start_time: float) -> list[TextContent]:
    solution_path = arguments.get("solutionPath", "") or ""
    tc_version = arguments.get("tcVersion")
    stop_on_error = arguments.get("stopOnError", True)
    timeout_minutes = int(arguments.get("timeoutMinutes", 15))
    steps = arguments.get("steps", []) or []
    confirm = arguments.get("confirm", "")

    if not isinstance(steps, list) or len(steps) == 0:
        return [TextContent(type="text", text="❌ twincat_batch requires a non-empty 'steps' list.")]

    # Validate each step and collect the set of commands used so we can
    # do batch-aware safety checks.
    step_commands: list[str] = []
    for i, step in enumerate(steps):
        if not isinstance(step, dict):
            return [TextContent(type="text", text=f"❌ Step #{i} is not an object.")]
        cmd = (step.get("command") or "").strip().lower()
        if not cmd:
            return [TextContent(type="text", text=f"❌ Step #{i} is missing 'command'.")]
        step_commands.append(cmd)

    # Batch-aware armed-mode check: if any step is dangerous, the whole
    # batch must be armed.
    dangerous_in_batch = sorted({c for c in step_commands if c in DANGEROUS_BATCH_COMMANDS})
    if dangerous_in_batch and not is_armed():
        return [TextContent(type="text", text=(
            f"🔒 SAFETY: twincat_batch contains dangerous step(s): "
            f"{', '.join(dangerous_in_batch)}.\n\n"
            f"The server is currently in SAFE mode. To run this batch:\n"
            f"1. Call 'twincat_arm_dangerous_operations' with a reason\n"
            f"2. Then retry this batch within {ARMED_MODE_TTL} seconds\n\n"
            f"This safety mechanism prevents accidental PLC modifications."
        ))]

    # Batch-aware confirmation check: activate/restart inside a batch
    # still need an explicit 'CONFIRM'.
    confirm_required_in_batch = sorted({c for c in step_commands if c in CONFIRMATION_REQUIRED_BATCH_COMMANDS})
    if confirm_required_in_batch and confirm != CONFIRM_TOKEN:
        return [TextContent(type="text", text=(
            f"⚠️ CONFIRMATION REQUIRED for twincat_batch\n\n"
            f"This batch contains step(s): {', '.join(confirm_required_in_batch)} "
            f"which will affect the target PLC.\n\n"
            f"To proceed, add the parameter:\n"
            f"  confirm: \"{CONFIRM_TOKEN}\"\n\n"
            f"This ensures intentional execution of destructive operations."
        ))]

    # Build the batch input JSON the C# BatchCommand expects.
    batch_input = {
        "stopOnError": bool(stop_on_error),
        "steps": [
            {
                "id": step.get("id"),
                "command": step.get("command"),
                "args": step.get("args", {}) or {},
            }
            for step in steps
        ],
    }
    if solution_path:
        batch_input["solutionPath"] = solution_path
    if tc_version:
        batch_input["tcVersion"] = tc_version

    # Write to a temp file (safer than stdin for larger batches and
    # keeps the JSON easy to inspect on failure).
    tmp_file = tempfile.NamedTemporaryFile(
        mode="w", suffix=".json", prefix="tc-batch-", delete=False, encoding="utf-8"
    )
    try:
        json.dump(batch_input, tmp_file, indent=2)
        tmp_file.flush()
        tmp_file.close()

        result, progress_messages = run_tc_automation_with_progress(
            "batch", ["--input", tmp_file.name], timeout_minutes
        )
    finally:
        try:
            os.unlink(tmp_file.name)
        except Exception:
            pass

    total_steps = result.get("totalSteps", len(steps))
    completed = result.get("completedSteps", 0)
    failed_index = result.get("failedStepIndex", -1)
    vs_open_ms = result.get("vsOpenDurationMs", 0)
    total_ms = result.get("totalDurationMs", 0)
    overall_success = result.get("success", False)

    if overall_success:
        header = f"✅ Batch completed: {completed}/{total_steps} step(s) succeeded"
    else:
        stopped_at = result.get("stoppedAt") or (
            f"step{failed_index + 1}" if failed_index >= 0 else "unknown"
        )
        header = (
            f"❌ Batch failed at step '{stopped_at}' "
            f"({completed}/{total_steps} completed)"
        )

    output = f"{header}\n\n"
    if vs_open_ms:
        output += f"🪟 Shell open:  {format_duration(vs_open_ms / 1000.0)}\n"
    if total_ms:
        output += f"⏱️ Batch total: {format_duration(total_ms / 1000.0)}\n"

    if progress_messages:
        output += "\n📋 Execution Log:\n"
        for msg in progress_messages:
            output += f"  ▸ {msg}\n"

    step_results = result.get("results", [])
    if step_results:
        output += "\n📦 Step Results:\n"
        for sr in step_results:
            icon = "✅" if sr.get("success") else "❌"
            sid = sr.get("id") or f"step{(sr.get('index', 0)) + 1}"
            cmd = sr.get("command", "")
            dur = sr.get("durationMs", 0)
            output += f"  {icon} [{sid}] {cmd}  ({format_duration(dur / 1000.0)})\n"
            if not sr.get("success"):
                err = sr.get("error") or "(no error message)"
                output += f"      ⚠️ {err}\n"

            inner = sr.get("result")
            # Surface interesting payload fields for common commands so the
            # agent can act on them without another call.
            if isinstance(inner, dict):
                if cmd == "build":
                    errs = _ci_get(inner, "errors") or []
                    warns = _ci_get(inner, "warnings") or []
                    if errs:
                        output += f"      🔴 {len(errs)} error(s):\n"
                        for e in errs[:5]:
                            output += f"         - {_ci_get(e, 'fileName', 'file', default='')}:{_ci_get(e, 'line', default='')}: {_ci_get(e, 'description', 'message', default='')}\n"
                        if len(errs) > 5:
                            output += f"         ... and {len(errs) - 5} more\n"
                    if warns:
                        output += f"      🟡 {len(warns)} warning(s)\n"
                elif cmd == "info":
                    output += (
                        f"      TwinCAT: {_ci_get(inner, 'tcVersion', default='?')} | "
                        f"Platform: {_ci_get(inner, 'targetPlatform', default='?')}\n"
                    )
                    for plc in _ci_get(inner, "plcProjects") or []:
                        output += f"        - {_ci_get(plc, 'name', default='?')} (port {_ci_get(plc, 'amsPort', default='?')})\n"
                elif cmd == "list-plcs":
                    for plc in _ci_get(inner, "plcProjects") or []:
                        output += (
                            f"        - {_ci_get(plc, 'name', default='?')} "
                            f"(port {_ci_get(plc, 'amsPort', default='?')}, "
                            f"boot={_ci_get(plc, 'bootProjectAutostart')})\n"
                        )
                elif cmd == "list-tasks":
                    for t in _ci_get(inner, "tasks") or []:
                        cycle_us = _ci_get(t, "cycleTimeUs", default=0)
                        output += (
                            f"        - {_ci_get(t, 'name', default='?')}  "
                            f"cycle={cycle_us}µs  "
                            f"enabled={not _ci_get(t, 'disabled', default=True)}  "
                            f"autostart={_ci_get(t, 'autoStart', default=False)}\n"
                        )
                elif cmd == "get-state":
                    output += (
                        f"      State: {_ci_get(inner, 'adsState', default='?')}  "
                        f"({_ci_get(inner, 'stateDescription', default='')})\n"
                    )
                elif cmd == "read-var":
                    output += (
                        f"      {_ci_get(inner, 'dataType', default='?')}  "
                        f"value=`{_ci_get(inner, 'value')}`\n"
                    )
                elif cmd == "write-var":
                    output += (
                        f"      {_ci_get(inner, 'dataType', default='?')}  "
                        f"prev=`{_ci_get(inner, 'previousValue')}`  "
                        f"new=`{_ci_get(inner, 'newValue')}`\n"
                    )
                elif cmd == "set-variant":
                    output += (
                        f"      variant: {_ci_get(inner, 'previousVariant') or '(default)'} "
                        f"-> {_ci_get(inner, 'currentVariant') or '(default)'}\n"
                    )
                elif cmd == "generate-library":
                    out_path = _ci_get(inner, "outputLibraryPath")
                    if out_path:
                        output += f"      output: {out_path}\n"

    # Top-level error from C# (e.g. couldn't open shell).
    if not overall_success and result.get("errorMessage"):
        output += f"\n💥 Error: {result['errorMessage']}\n"
        err_msg = str(result.get("errorMessage", ""))
        if "0x800706BE" in err_msg or "RPC" in err_msg or " COM" in err_msg:
            output += (
                "\n💡 This looks like a stale TcXaeShell/devenv holding COM locks.\n"
                "   Run `twincat_kill_stale` and retry this batch.\n"
            )

    return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]
