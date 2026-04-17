"""
TwinCAT MCP Server

This MCP server exposes TwinCAT automation tools to AI assistants like GitHub Copilot.
It wraps the TcAutomation.exe CLI tool which provides access to the TwinCAT Automation Interface.

Tools:
- twincat_batch: Run an ordered sequence of TwinCAT operations against a single shared TcXaeShell (collapses VS startup cost across many steps)
- twincat_build: Build a TwinCAT solution and return errors/warnings
- twincat_get_info: Get information about a TwinCAT solution
- twincat_clean: Clean a TwinCAT solution
- twincat_set_target: Set target AMS Net ID
- twincat_activate: Activate configuration on target PLC
- twincat_restart: Restart TwinCAT runtime on target
- twincat_deploy: Full deployment workflow
- twincat_list_plcs: List all PLC projects in a solution
- twincat_set_boot_project: Configure boot project settings
- twincat_disable_io: Disable/enable I/O devices
- twincat_set_variant: Get or set TwinCAT project variant
- twincat_get_state: Get TwinCAT runtime state via ADS
- twincat_set_state: Set TwinCAT runtime state (Run/Stop/Config) via ADS
- twincat_read_var: Read a PLC variable via ADS
- twincat_write_var: Write a PLC variable via ADS
- twincat_list_tasks: List real-time tasks
- twincat_configure_task: Configure task (enable/autostart)
- twincat_configure_rt: Configure real-time CPU settings
- twincat_check_all_objects: Check all PLC objects including unused ones
- twincat_static_analysis: Run static code analysis (requires TE1200)
- twincat_generate_library: Export a PLC project as a TwinCAT .library artifact
- twincat_list_routes: List available ADS routes (PLCs)
- twincat_get_error_list: Get VS Error List contents (errors, warnings, messages)
- twincat_run_tcunit: Run TcUnit tests and return results
"""

import time

from mcp.server import Server
from mcp.server.stdio import stdio_server
from mcp.types import TextContent

# Modular internals. Everything under `twincat_mcp/` was extracted out of
# this file in a pure refactor — behavior is unchanged. See
# `twincat_mcp/__init__.py` for the package layout.
#
# This file is now a thin entry point:
#   - `list_tools`  advertises schemas from `twincat_mcp.tools.schemas`
#   - `call_tool`   runs the safety gates, then dispatches to a registered
#                   handler in `twincat_mcp.handlers.*`.
# Importing `twincat_mcp.handlers` is what populates `HANDLERS` (each
# submodule registers its tools at import time).
from twincat_mcp.handlers import HANDLERS
from twincat_mcp.safety import check_armed_for_tool, check_confirmation
from twincat_mcp.tools.schemas import get_tool_schemas

# Initialize MCP server.
server = Server("twincat-mcp")



@server.list_tools()
async def list_tools() -> list:
    """Advertise available TwinCAT tools (schemas live in twincat_mcp.tools.schemas)."""
    return get_tool_schemas()


@server.call_tool()
async def call_tool(name: str, arguments: dict) -> list[TextContent]:
    """
    Dispatch an MCP tool call to its registered handler.

    The flow is:
      1) Special-case `twincat_arm_dangerous_operations` — it toggles the
         gate itself, so it runs *before* the gate.
      2) Run the armed-mode gate for dangerous tools (write/deploy/etc).
      3) Run the confirmation gate for highly destructive tools.
      4) Look up the handler in `HANDLERS` and await it.
      5) Unknown tools fall through to a clear error message.
    """

    arm_handler = HANDLERS.get("twincat_arm_dangerous_operations")
    if name == "twincat_arm_dangerous_operations" and arm_handler is not None:
        return await arm_handler(arguments, time.time())

    allowed, message = check_armed_for_tool(name, arguments)
    if not allowed:
        return [TextContent(type="text", text=message)]

    confirmed, conf_message = check_confirmation(name, arguments)
    if not confirmed:
        return [TextContent(type="text", text=conf_message)]

    handler = HANDLERS.get(name)
    if handler is None:
        return [TextContent(type="text", text=f"Unknown tool: {name}")]

    tool_start_time = time.time()
    return await handler(arguments, tool_start_time)


async def main():
    """Run the MCP server."""
    async with stdio_server() as (read_stream, write_stream):
        await server.run(
            read_stream,
            write_stream,
            server.create_initialization_options()
        )


if __name__ == "__main__":
    import asyncio
    asyncio.run(main())
