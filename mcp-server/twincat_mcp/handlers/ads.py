"""
ADS communication handlers. These talk to a running TwinCAT runtime over
the ADS protocol; they do not need the solution loaded, but they still
route through `run_shell_step` so we can reuse the persistent host and
its CLI-fallback on crash.

Note: the C# commands for this family emit PascalCase JSON keys
(Success, AdsState, etc.), so the formatters below use PascalCase too.

Handlers covered: twincat_get_state, twincat_set_state,
twincat_read_var, twincat_write_var, twincat_ping_target,
twincat_list_symbols, twincat_read_plc_log.
"""

from mcp.types import TextContent

from ..defaults import resolve_ams_net_id
from ..dispatch import run_shell_step
from ..formatting import add_timing_to_output
from ._registry import register


@register("twincat_get_state")
async def handle_get_state(arguments: dict, tool_start_time: float) -> list[TextContent]:
    ams_net_id = resolve_ams_net_id(arguments.get("amsNetId"))
    port = arguments.get("port", 851)

    result, _ = run_shell_step(
        "get-state", {"amsNetId": ams_net_id, "port": port},
        timeout_minutes=1,
    )

    if result.get("Success"):
        state = result.get("AdsState", "Unknown")
        device_state = result.get("DeviceState", 0)
        emoji = "🟢" if state == "Run" else "🟡" if state == "Config" else "🔴" if state in ["Stop", "Error"] else "⚪"
        output = f"{emoji} TwinCAT State: **{state}**\n"
        output += f"📡 AMS Net ID: {result.get('AmsNetId', ams_net_id)}\n"
        output += f"🔌 Port: {result.get('Port', port)}\n"
        output += f"📊 Device State: {device_state}\n"
        output += f"📝 Description: {result.get('StateDescription', '')}"
    else:
        output = f"❌ Failed: {result.get('ErrorMessage', 'Unknown error')}"

    return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]


@register("twincat_set_state")
async def handle_set_state(arguments: dict, tool_start_time: float) -> list[TextContent]:
    ams_net_id = resolve_ams_net_id(arguments.get("amsNetId"))
    state = arguments.get("state", "")
    port = arguments.get("port", 851)

    result, _ = run_shell_step(
        "set-state", {"amsNetId": ams_net_id, "state": state, "port": port},
        timeout_minutes=1,
    )

    if result.get("Success"):
        prev_state = result.get("PreviousState", "Unknown")
        curr_state = result.get("CurrentState", "Unknown")
        emoji = "🟢" if curr_state == "Run" else "🟡" if curr_state == "Config" else "🔴" if curr_state in ["Stop", "Error"] else "⚪"
        output = f"{emoji} TwinCAT State Changed\n\n"
        output += f"📡 AMS Net ID: {result.get('AmsNetId', ams_net_id)}\n"
        output += f"🔄 Previous: {prev_state}\n"
        output += f"✅ Current: **{curr_state}**\n"
        output += f"📝 {result.get('StateDescription', '')}"
        if result.get("Warning"):
            output += f"\n⚠️ {result.get('Warning')}"
    else:
        output = f"❌ Failed to set state: {result.get('ErrorMessage', 'Unknown error')}"

    return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]


@register("twincat_read_var")
async def handle_read_var(arguments: dict, tool_start_time: float) -> list[TextContent]:
    ams_net_id = resolve_ams_net_id(arguments.get("amsNetId"))
    symbol = arguments.get("symbol", "")
    port = arguments.get("port", 851)

    result, _ = run_shell_step(
        "read-var", {"amsNetId": ams_net_id, "symbol": symbol, "port": port},
        timeout_minutes=1,
    )

    if result.get("Success"):
        output = f"✅ Variable Read: **{symbol}**\n\n"
        output += f"📊 Value: `{result.get('Value', 'null')}`\n"
        output += f"📋 Data Type: {result.get('DataType', 'Unknown')}\n"
        output += f"📐 Size: {result.get('Size', 0)} bytes"
    else:
        output = f"❌ Failed to read '{symbol}': {result.get('ErrorMessage', 'Unknown error')}"

    return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]


@register("twincat_list_symbols")
async def handle_list_symbols(arguments: dict, tool_start_time: float) -> list[TextContent]:
    ams_net_id = resolve_ams_net_id(arguments.get("amsNetId"))
    port = arguments.get("port", 851)
    prefix = arguments.get("prefix")
    contains = arguments.get("contains")
    max_results = arguments.get("max", 200)
    include_types = arguments.get("includeTypes", False)

    step_args: dict = {
        "amsNetId": ams_net_id,
        "port": port,
        "max": int(max_results),
        "includeTypes": bool(include_types),
    }
    if prefix:
        step_args["prefix"] = str(prefix)
    if contains:
        step_args["contains"] = str(contains)

    result, _ = run_shell_step(
        "list-symbols", step_args, timeout_minutes=1,
    )

    if not result.get("Success"):
        err = result.get("ErrorMessage", "Unknown error")
        output = f"❌ Failed to enumerate symbols: {err}"
        state = result.get("TargetState")
        if state and state not in ("Run", "Stop"):
            output += (
                f"\n\n💡 Target is in state '{state}'. Symbol enumeration "
                "needs the runtime in Run or Stop. If the target is "
                "rebooting after an activate/restart, retry in a few "
                "seconds (twincat_ping_target can tell you when it's "
                "back)."
            )
        return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]

    total_matched = result.get("TotalMatched", 0)
    total_scanned = result.get("TotalScanned", 0)
    truncated = result.get("Truncated", False)
    symbols = result.get("Symbols", [])

    output = f"🔎 Symbol listing on {ams_net_id}:{port}\n"
    output += f"📊 Matched {total_matched} of {total_scanned} scanned\n"
    if truncated:
        output += (
            f"⚠️  Truncated to {len(symbols)} entries — raise `max` or "
            "tighten `prefix`/`contains` to see the rest.\n"
        )
    output += "\n"

    if not symbols:
        output += (
            "(no matches)\n\n"
            "💡 If you expected symbols from a specific project (e.g. a "
            "TcUnit test suite) and got nothing, the runtime may have a "
            "different project loaded than you think. `twincat_get_info` "
            "on the solution tells you what port 851 should contain.\n"
        )
    else:
        for sym in symbols:
            name = sym.get("Name", "")
            type_name = sym.get("TypeName")
            if type_name:
                size = sym.get("Size", 0)
                output += f"  • {name} : {type_name} ({size}B)\n"
            else:
                output += f"  • {name}\n"

    return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]


@register("twincat_read_plc_log")
async def handle_read_plc_log(arguments: dict, tool_start_time: float) -> list[TextContent]:
    ams_net_id = resolve_ams_net_id(arguments.get("amsNetId"))
    wait_seconds = arguments.get("waitSeconds", 5)
    contains = arguments.get("contains")
    max_results = arguments.get("max", 200)

    step_args: dict = {
        "amsNetId": ams_net_id,
        "waitSeconds": int(wait_seconds),
        "max": int(max_results),
    }
    if contains:
        step_args["contains"] = str(contains)

    result, _ = run_shell_step(
        "read-plc-log", step_args,
        # Give the CLI fallback enough headroom to actually complete the
        # listening window. step_args.waitSeconds is the listen duration;
        # add a minute of overhead for connect/teardown.
        timeout_minutes=max(2, int(wait_seconds) // 60 + 2),
    )

    if not result.get("Success"):
        err = result.get("ErrorMessage", "Unknown error")
        output = f"❌ Failed to read PLC log: {err}\n"
        output += (
            "\n💡 Common causes: target is unreachable (try "
            "`twincat_ping_target`), the TcEventLogger COM proxy isn't "
            "registered on this machine, or the target doesn't have the "
            "TwinCAT 3 event logger enabled."
        )
        return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]

    total = result.get("TotalCaptured", 0)
    truncated = result.get("Truncated", False)
    events = result.get("Events", [])

    output = f"📜 PLC event log on {ams_net_id}  "
    output += f"(listened {wait_seconds}s, captured {total}"
    if contains:
        output += f" matching '{contains}'"
    output += ")\n"
    if truncated:
        output += f"⚠️  Truncated to {len(events)} — raise `max` to see more.\n"
    output += "\n"

    if not events:
        output += "(no events in window)\n"
    else:
        for e in events:
            kind = e.get("Kind", "")
            sev = e.get("Severity") or ""
            ts = e.get("Timestamp", "")
            text = e.get("Text", "")
            sev_prefix = f"[{sev}]" if sev else ""
            kind_icon = {
                "Message": "💬",
                "AlarmRaised": "🚨",
                "AlarmCleared": "✅",
                "AlarmConfirmed": "🔕",
            }.get(kind, "•")
            output += f"  {kind_icon} {ts} {sev_prefix} {text}\n"

    return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]


@register("twincat_ping_target")
async def handle_ping_target(arguments: dict, tool_start_time: float) -> list[TextContent]:
    ams_net_id = resolve_ams_net_id(arguments.get("amsNetId"))
    port = arguments.get("port", 851)
    timeout_ms = arguments.get("timeoutMs", 2500)

    result, _ = run_shell_step(
        "ping-target", {
            "amsNetId": ams_net_id, "port": port, "timeoutMs": timeout_ms
        },
        # Two probes × timeoutMs + overhead — keep this well under the
        # default step timeout so the MCP call can't itself hang forever
        # when the target is truly gone.
        timeout_minutes=1,
    )

    if not result.get("Success"):
        output = (
            f"❌ Ping probe crashed: {result.get('ErrorMessage', 'Unknown error')}"
        )
        return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]

    classification = result.get("Classification", "unknown")
    message = result.get("Message", "")
    sys_reachable = result.get("SystemServiceReachable", False)
    rt_reachable = result.get("RuntimeReachable", False)
    rt_state = result.get("RuntimeState") or "(no response)"
    sys_ms = result.get("SystemServiceDurationMs", 0)
    rt_ms = result.get("RuntimeDurationMs", 0)

    icon = {
        "reachable": "🟢",
        "rebooting": "🟡",
        "unreachable": "🔴",
        "routeMissing": "🟠",
    }.get(classification, "⚪")

    output = f"{icon} Ping {ams_net_id}  →  **{classification}**\n\n"
    output += f"{message}\n\n"
    output += "Probe detail:\n"
    output += (
        f"  • System service (port 10000): "
        f"{'✅' if sys_reachable else '❌'}  ({sys_ms}ms)"
    )
    if result.get("SystemServiceError"):
        output += f"  — {result.get('SystemServiceError')}"
    output += "\n"
    output += (
        f"  • PLC runtime (port {port}):    "
        f"{'✅ ' + rt_state if rt_reachable else '❌'}  ({rt_ms}ms)"
    )
    if result.get("RuntimeError"):
        output += f"  — {result.get('RuntimeError')}"
    output += "\n"

    return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]


@register("twincat_write_var")
async def handle_write_var(arguments: dict, tool_start_time: float) -> list[TextContent]:
    ams_net_id = resolve_ams_net_id(arguments.get("amsNetId"))
    symbol = arguments.get("symbol", "")
    value = arguments.get("value", "")
    port = arguments.get("port", 851)

    result, _ = run_shell_step(
        "write-var", {
            "amsNetId": ams_net_id, "symbol": symbol,
            "value": value, "port": port,
        },
        timeout_minutes=1,
    )

    if result.get("Success"):
        output = f"✅ Variable Written: **{symbol}**\n\n"
        output += f"📝 Previous: `{result.get('PreviousValue', 'unknown')}`\n"
        output += f"📝 New Value: `{result.get('NewValue', value)}`\n"
        output += f"📋 Data Type: {result.get('DataType', 'Unknown')}"
    else:
        output = f"❌ Failed to write '{symbol}': {result.get('ErrorMessage', 'Unknown error')}"

    return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]
