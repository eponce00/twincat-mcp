"""
TwinCAT Scope handlers.

Two recording paths are exposed:
  1. twincat_ads_record  — see handlers/ads.py (no license needed, preferred)
  2. twincat_scope_*     — requires TE13xx Scope Server license

The Scope Server tools (start/stop/status) communicate with a long-lived
TcAutomation.exe `scope-session` subprocess via JSON over stdin/stdout,
managed by the ScopeSession class below.

twincat_scope_create_config and twincat_scope_export call TcAutomation.exe
directly as one-shot CLI invocations.
"""

import json
import os
import subprocess
import threading
from datetime import datetime

from mcp.types import TextContent

from ..cli import find_tc_automation_exe, run_tc_automation
from ..formatting import add_timing_to_output
from ._registry import register


# ---------------------------------------------------------------------------
# Persistent scope session
# ---------------------------------------------------------------------------

class ScopeSession:
    """Manages a persistent TcAutomation.exe scope-session subprocess."""

    def __init__(self):
        self.process: subprocess.Popen | None = None
        self._lock = threading.RLock()

    def _start(self):
        exe_path = find_tc_automation_exe()
        self.process = subprocess.Popen(
            [str(exe_path), "scope-session"],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            cwd=str(exe_path.parent),
        )
        ready_line = self.process.stdout.readline()
        if ready_line:
            try:
                ready = json.loads(ready_line)
                if not ready.get("success"):
                    raise RuntimeError(
                        f"Scope session failed to start: {ready.get('errorMessage', 'unknown error')}"
                    )
            except json.JSONDecodeError:
                raise RuntimeError(
                    f"Scope session returned invalid ready signal: {ready_line!r}"
                )

    def ensure_started(self):
        with self._lock:
            if self.process is None or self.process.poll() is not None:
                self._start()

    def send_command(self, command: dict, timeout_seconds: int = 30) -> dict:
        with self._lock:
            self.ensure_started()

            assert self.process is not None
            self.process.stdin.write(json.dumps(command) + "\n")
            self.process.stdin.flush()

            result_holder: list[str | None] = [None]

            def _read():
                result_holder[0] = self.process.stdout.readline()

            reader = threading.Thread(target=_read, daemon=True)
            reader.start()
            reader.join(timeout=timeout_seconds)

            if reader.is_alive():
                self.process.kill()
                self.process = None
                raise RuntimeError(f"Scope session timed out after {timeout_seconds}s")

            line = result_holder[0]
            if not line:
                self.process = None
                raise RuntimeError("Scope session process ended unexpectedly")
            return json.loads(line)

    def close(self):
        with self._lock:
            if self.process and self.process.poll() is None:
                try:
                    self.send_command({"command": "exit"})
                    self.process.wait(timeout=5)
                except Exception:
                    self.process.kill()
                self.process = None

    @property
    def is_running(self) -> bool:
        return self.process is not None and self.process.poll() is None


# Module-level singleton
_scope_session = ScopeSession()


# ---------------------------------------------------------------------------
# Handlers
# ---------------------------------------------------------------------------

@register("twincat_scope_create_config")
async def handle_scope_create_config(arguments: dict, tool_start_time: float) -> list[TextContent]:
    ams_net_id = arguments.get("amsNetId", "")
    port = arguments.get("port", 851)
    variables: list = arguments.get("variables", [])
    sample_time_ms = arguments.get("sampleTimeMs", 10)
    record_time_sec = arguments.get("recordTimeSec")
    output_path = arguments.get("outputPath") or os.path.join(
        os.environ.get("TEMP", "/tmp"),
        f"scope_{datetime.now():%Y%m%d_%H%M%S}.tcscopex"
    )
    chart_name = arguments.get("chartName", "MCP Trace")

    args = [
        "--amsnetid", ams_net_id,
        "--port", str(port),
        "--variables", ",".join(variables),
        "--sampletime", str(sample_time_ms),
        "--output", output_path,
        "--chartname", chart_name,
    ]
    if record_time_sec is not None:
        args.extend(["--recordtime", str(record_time_sec)])

    result = run_tc_automation("scope-create", args)

    if result.get("success"):
        output = f"✅ Scope Configuration Created\n\n"
        output += f"📁 Config: `{result.get('configPath', output_path)}`\n"
        output += f"📡 Target: {ams_net_id}:{port}\n"
        output += f"⏱ Sample rate: {sample_time_ms}ms\n"
        if record_time_sec:
            output += f"🕐 Record time: {record_time_sec}s\n"
        output += f"📊 Variables: {', '.join(variables)}\n\n"
        output += "To record data, use `twincat_ads_record` (preferred, no license needed) "
        output += "or `twincat_scope_start_record` (requires TE13xx license)."
    else:
        error = result.get("errorMessage", "Unknown error")
        output = f"❌ Failed to create scope config: {error}"

    return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]


@register("twincat_scope_start_record")
async def handle_scope_start_record(arguments: dict, tool_start_time: float) -> list[TextContent]:
    config_path = arguments.get("configPath", "")

    try:
        resp = _scope_session.send_command({"command": "start", "configPath": config_path})
        if resp.get("success"):
            output = f"🔴 Scope Recording Started\n\n"
            output += f"📁 Config: `{resp.get('configPath', config_path)}`\n"
            output += f"🕐 Started at: {resp.get('startedAt', 'now')}\n\n"
            output += "Use `twincat_scope_get_status` to check progress.\n"
            output += "Use `twincat_scope_stop_record` to stop and export data."
        else:
            error = resp.get("errorMessage", "Unknown error")
            output = f"❌ Failed to start scope recording: {error}"
    except Exception as e:
        output = f"❌ Scope session error: {e}"

    return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]


@register("twincat_scope_stop_record")
async def handle_scope_stop_record(arguments: dict, tool_start_time: float) -> list[TextContent]:
    output_path = arguments.get("outputPath", "")
    fmt = arguments.get("format", "csv")

    try:
        resp = _scope_session.send_command(
            {"command": "stop", "outputPath": output_path, "format": fmt},
            timeout_seconds=60,
        )
        if resp.get("success"):
            csv_path = resp.get("dataPath") or resp.get("outputPath") or output_path
            output = f"⏹ Scope Recording Stopped\n\n"
            output += f"💾 Data saved to: `{csv_path}`\n"
            output += f"📊 Duration: {resp.get('elapsedSeconds', '?')}s\n"
            output += f"📈 Samples: {resp.get('samplesCollected', '?')}"
        else:
            error = resp.get("errorMessage", "Unknown error")
            output = f"❌ Failed to stop scope recording: {error}"
    except Exception as e:
        output = f"❌ Scope session error: {e}"

    return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]


@register("twincat_scope_get_status")
async def handle_scope_get_status(arguments: dict, tool_start_time: float) -> list[TextContent]:
    if not _scope_session.is_running:
        return [TextContent(type="text", text=add_timing_to_output(
            "⚪ Scope session not running. No recording active.", tool_start_time
        ))]

    try:
        resp = _scope_session.send_command({"command": "status"})
        if resp.get("success"):
            state = resp.get("state", "Unknown")
            is_recording = state.lower() == "record"
            emoji = "🔴" if is_recording else "⚪"
            output = f"{emoji} Scope Status\n\n"
            output += f"State: {state}\n"
            output += f"⏱ Elapsed: {resp.get('elapsedSeconds', '?')}s\n"
            output += f"📊 Channels: {resp.get('channelCount', '?')}"
        else:
            output = f"❌ {resp.get('errorMessage', 'Unknown error')}"
    except Exception as e:
        output = f"❌ Scope session error: {e}"

    return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]


@register("twincat_scope_export")
async def handle_scope_export(arguments: dict, tool_start_time: float) -> list[TextContent]:
    input_path = arguments.get("inputPath", "")
    output_path = arguments.get("outputPath", "")
    fmt = arguments.get("format", "csv")

    args = ["--input", input_path, "--format", fmt]
    if output_path:
        args.extend(["--output", output_path])

    result = run_tc_automation("scope-export", args)

    if result.get("success"):
        out = result.get("outputPath", output_path)
        output = f"✅ Scope Export Complete\n\n"
        output += f"📁 Source: `{input_path}`\n"
        output += f"💾 Exported to: `{out}`\n"
        output += f"📄 Format: {fmt}"
    else:
        error = result.get("errorMessage", "Unknown error")
        output = f"❌ Scope export failed: {error}"

    return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]
