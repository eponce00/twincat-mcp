"""
Safety / meta handlers:
  - twincat_arm_dangerous_operations   arm or disarm the dangerous-op gate
  - twincat_kill_stale                 surgical cleanup of our own host + orphans
  - twincat_host_status                report persistent-host state (read-only)
  - twincat_list_routes                list ADS routes from StaticRoutes.xml
  - twincat_set_default_target         change the persistent default PLC

None of these go through `run_shell_step` — they're either pure Python,
file reads, or direct subprocess calls against the CLI helpers.
"""

import json
import os
import subprocess
import xml.etree.ElementTree as ET
from pathlib import Path

from mcp.types import TextContent

from ..cli import find_tc_automation_exe
from ..defaults import (
    clear_persistent_default,
    get_default_status,
    set_persistent_default,
)
from ..formatting import add_timing_to_output
from ..host import HOST_DISABLED, drop_shell_host, get_shell_host_if_alive
from ..safety import arm_dangerous_operations, disarm_dangerous_operations
from ._registry import register


@register("twincat_arm_dangerous_operations")
async def handle_arm(arguments: dict, tool_start_time: float) -> list[TextContent]:
    """
    Arm/disarm the dangerous-operations gate.

    This handler is special: it sits outside the armed-mode gate itself
    (dispatched ahead of the gate in server.py's call_tool), because it's
    the thing that *toggles* the gate. It also doesn't append a timing
    footer — the armed-mode status IS the output.
    """
    disarm = arguments.get("disarm", False)

    if disarm:
        disarm_dangerous_operations()
        output = "🔒 Dangerous operations DISARMED\n\nThe server is now in SAFE mode."
    else:
        reason = arguments.get("reason", "No reason provided")
        result = arm_dangerous_operations(reason)
        output = (
            f"⚠️ Dangerous operations ARMED\n\n"
            f"🕐 TTL: {result['ttl_seconds']} seconds\n"
            f"📝 Reason: {result['reason']}\n\n"
            f"The following tools are now available:\n"
            f"  • twincat_activate\n"
            f"  • twincat_restart\n"
            f"  • twincat_deploy\n"
            f"  • twincat_set_state\n"
            f"  • twincat_write_var\n\n"
            f"⏰ Armed mode will automatically expire in {result['ttl_seconds']} seconds."
        )

    return [TextContent(type="text", text=output)]


@register("twincat_kill_stale")
async def handle_kill_stale(arguments: dict, tool_start_time: float) -> list[TextContent]:
    """
    SURGICAL cleanup: NEVER kill TcXaeShell / devenv by image name.
    Doing so closes the user's interactive IDE, their Visual Studio, and
    every other automation's shell on the box.

    Safety model (defense in depth):
      1) Kill our own persistent shell host + its DTE (we own those PIDs).
      2) Run the janitor for session files from *crashed* MCP instances.
         Those are guaranteed-dead MCPs; their hosts/DTEs are orphans.
      3) NO title-based "headless sweep". The heuristic is unsafe —
         a user-opened TcXaeShell can legitimately report an empty title
         during startup or when a modal dialog is active, and we'd kill
         their IDE. If reap-orphans couldn't identify it via a session
         file, it isn't ours.
    """
    output_parts: list[str] = []
    killed_own_dte_pid = None

    host = get_shell_host_if_alive()  # never triggers lazy start
    if host is not None and host.is_alive():
        # Capture the DTE PID from status BEFORE shutting down, so we can
        # force-kill in case graceful Quit hangs.
        dte_pid = None
        try:
            st = host.status()
            dte_pid = st.get("dtePid") if isinstance(st, dict) else None
        except Exception:
            dte_pid = None
        try:
            host.shutdown(timeout=8.0)
        except Exception:
            pass
        drop_shell_host()

        if dte_pid:
            try:
                subprocess.run(
                    ["taskkill", "/F", "/PID", str(dte_pid)],
                    capture_output=True, text=True, timeout=10,
                    creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
                )
                killed_own_dte_pid = dte_pid
            except Exception:
                pass
        output_parts.append(
            "🔪 Shut down our own shell host"
            + (f" (DTE PID {killed_own_dte_pid})" if killed_own_dte_pid else "")
        )

    # Run the C# janitor explicitly via `reap-orphans`. It walks the
    # session files from crashed MCPs and safely kills matching orphans
    # (start-time verified, so a reused PID never hits the wrong process).
    try:
        exe = find_tc_automation_exe()
        janitor = subprocess.run(
            [str(exe), "reap-orphans"],
            capture_output=True, text=True, timeout=30,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        reaped = 0
        try:
            payload = json.loads((janitor.stdout or "").strip() or "{}")
            reaped = int(payload.get("reaped", 0))
        except Exception:
            pass
        if reaped > 0:
            output_parts.append(f"🧹 Janitor reaped {reaped} orphaned session(s)")
        else:
            output_parts.append("🧹 Janitor: no orphaned sessions found")
    except Exception as e:
        output_parts.append(f"⚠️ Janitor sweep skipped: {e}")

    if not output_parts:
        output_parts.append("✅ Nothing to clean up. Your Visual Studio / TcXaeShell sessions were not touched.")
    else:
        output_parts.append(
            "\nℹ️ This tool never kills `TcXaeShell.exe` or `devenv.exe` by image name — "
            "your open IDE is safe."
        )

    return [TextContent(type="text", text=add_timing_to_output("\n".join(output_parts), tool_start_time))]


@register("twincat_host_status")
async def handle_host_status(arguments: dict, tool_start_time: float) -> list[TextContent]:
    """Report persistent-host state. Read-only; never spawns the host."""
    host = get_shell_host_if_alive()
    if host is None or not host.is_alive():
        if HOST_DISABLED:
            out = "⚫ Shell host: DISABLED (TWINCAT_DISABLE_HOST is set)"
        else:
            out = ("⚫ Shell host: not running\n\n"
                   "It will be started lazily on the next tool call that needs TcXaeShell. "
                   "Expect a one-time 25-90s cost for that first call; subsequent calls reuse the shell.")
        return [TextContent(type="text", text=add_timing_to_output(out, tool_start_time))]

    try:
        st = host.status()
    except Exception as e:
        return [TextContent(type="text", text=add_timing_to_output(f"❌ Failed to query host: {e}", tool_start_time))]

    st = st or {}
    lines = ["🟢 Shell host: RUNNING\n"]
    if st.get("hostPid") is not None:
        lines.append(f"  Host PID: {st.get('hostPid')}")
    if st.get("dtePid") is not None:
        lines.append(f"  DTE PID: {st.get('dtePid')}")
    if st.get("solutionPath"):
        lines.append(f"  Loaded solution: {st.get('solutionPath')}")
    else:
        lines.append("  Loaded solution: (none yet)")
    if st.get("uptimeSeconds") is not None:
        try:
            lines.append(f"  Uptime: {float(st.get('uptimeSeconds')):.0f}s")
        except Exception:
            pass
    if st.get("callsServed") is not None:
        lines.append(f"  Calls served: {st.get('callsServed')}")
    if st.get("startedUtc"):
        lines.append(f"  Started: {st.get('startedUtc')}")
    return [TextContent(type="text", text=add_timing_to_output("\n".join(lines), tool_start_time))]


@register("twincat_list_routes")
async def handle_list_routes(arguments: dict, tool_start_time: float) -> list[TextContent]:
    """List ADS routes from TwinCAT's StaticRoutes.xml (file read only)."""
    routes_file: Path | None = None

    tc_dir = os.environ.get("TWINCAT3DIR", "")
    if tc_dir:
        candidate = Path(tc_dir).parent / "3.1" / "Target" / "StaticRoutes.xml"
        if candidate.exists():
            routes_file = candidate

    if not routes_file:
        for base in ["C:\\TwinCAT", "C:\\Program Files\\Beckhoff\\TwinCAT"]:
            candidate = Path(base) / "3.1" / "Target" / "StaticRoutes.xml"
            if candidate.exists():
                routes_file = candidate
                break

    if not routes_file or not routes_file.exists():
        return [TextContent(type="text", text="❌ Could not find TwinCAT StaticRoutes.xml\n\nTip: Ensure TwinCAT 3.1 is installed.")]

    try:
        tree = ET.parse(routes_file)
        root = tree.getroot()

        routes = []
        for route in root.findall(".//Route"):
            name = route.find("Name")
            address = route.find("Address")
            netid = route.find("NetId")

            if name is not None and netid is not None:
                routes.append({
                    "name": name.text or "",
                    "address": address.text if address is not None else "",
                    "amsNetId": netid.text or "",
                })

        if not routes:
            output = "📡 No ADS routes configured\n\nTip: Add routes via TwinCAT Router or XAE."
        else:
            output = f"📡 Available ADS Routes ({len(routes)})\n\n"
            output += "| Name | Address | AMS Net ID |\n"
            output += "|------|---------|------------|\n"
            for r in routes:
                output += f"| {r['name']} | {r['address']} | {r['amsNetId']} |\n"
            output += f"\n📁 Source: {routes_file}"

    except Exception as e:
        output = f"❌ Failed to parse routes file: {str(e)}"

    return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]


@register("twincat_set_default_target")
async def handle_set_default_target(arguments: dict, tool_start_time: float) -> list[TextContent]:
    """
    Change (or clear) the persistent default AMS Net ID.

    The agent uses this when the user says "always target X from now on"
    — the value is written to the MCP config file under
    `%LOCALAPPDATA%\\twincat-mcp\\config.json` and survives across
    conversations and server restarts, so a later conversation inherits
    the new default without the user having to re-explain the setup.

    Two modes:
      - `amsNetId`: persist the given value as the new default
      - `reset: true`: remove the persisted value; the module then
        falls back to `TWINCAT_DEFAULT_AMS_NET_ID` env var, or
        hardcoded `127.0.0.1.1.1` if that isn't set either

    Not gated by armed mode — writing a config file doesn't touch any
    PLC. The destructive consequences (deploy/activate/etc) only kick
    in when a destructive tool is later invoked against that default,
    and those tools have their own arm + confirm gates.
    """
    reset = bool(arguments.get("reset", False))
    ams_net_id = (arguments.get("amsNetId") or "").strip()
    reason = arguments.get("reason")

    if not reset and not ams_net_id:
        # Read-only fallback: if the agent calls with neither, show the
        # current status so it can decide what to do next. Matches the
        # "no side effect without an argument" principle.
        status = get_default_status()
        output = _format_status(status, header="📍 Current default target PLC")
        return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]

    try:
        if reset:
            result = clear_persistent_default()
            if not result["hadPersistedValue"]:
                output = (
                    f"ℹ️ No persisted default was set — nothing to clear.\n\n"
                    f"Effective default is still **{result['newValue']}** "
                    f"(source: {result['newSource']})."
                )
            else:
                output = (
                    f"🧹 Cleared persisted default.\n\n"
                    f"  Previous: `{result['previousValue']}` (source: {result['previousSource']})\n"
                    f"  Now: **`{result['newValue']}`** (source: {result['newSource']})\n\n"
                    f"Config file: {result['configFile']}"
                )
        else:
            result = set_persistent_default(ams_net_id, reason=reason)
            output = (
                f"✅ Default target PLC updated.\n\n"
                f"  Previous: `{result['previousValue']}` (source: {result['previousSource']})\n"
                f"  Now: **`{result['newValue']}`** (source: config file)\n"
            )
            if reason:
                output += f"  Reason: {reason}\n"
            output += (
                f"\nPersisted to: {result['configFile']}\n"
                f"This new default applies to every tool that takes an `amsNetId` "
                f"and will survive into future conversations and server restarts. "
                f"The agent can still override per-call by passing `amsNetId` "
                f"explicitly."
            )
    except ValueError as e:
        output = f"❌ {e}"

    return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]


def _format_status(status: dict, header: str) -> str:
    """Human-readable rendering of `get_default_status()`."""
    out = (
        f"{header}\n\n"
        f"  Effective: **`{status['effective']}`** (source: {status['source']})\n\n"
        f"Sources (highest precedence first):\n"
        f"  1. Config file : `{status['configValue'] or '(unset)'}`  "
        f"→ {status['configFile']}\n"
        f"  2. {status['envVar']} : `{status['envValue'] or '(unset)'}`\n"
        f"  3. Hardcoded fallback : `{status['fallback']}`\n\n"
        f"Change with `twincat_set_default_target({{amsNetId: \"...\"}})` "
        f"or clear with `twincat_set_default_target({{reset: true}})`."
    )
    return out
