"""
ADS communication handlers. These talk to a running TwinCAT runtime over
the ADS protocol; they do not need the solution loaded, but they still
route through `run_shell_step` so we can reuse the persistent host and
its CLI-fallback on crash.

Note: the C# commands for this family emit PascalCase JSON keys
(Success, AdsState, etc.), so the formatters below use PascalCase too.

Handlers covered: twincat_get_state, twincat_set_state,
twincat_read_var, twincat_write_var.
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
