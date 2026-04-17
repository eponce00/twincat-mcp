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

import json
import os
import subprocess
import sys
import tempfile
import time
from pathlib import Path

from mcp.server import Server
from mcp.server.stdio import stdio_server
from mcp.types import TextContent

# Modular internals. Everything under `twincat_mcp/` was extracted out of
# this file in a pure refactor — behavior is unchanged. See
# twincat_mcp/__init__.py for the package layout.
from twincat_mcp.safety import (
    ARMED_MODE_TTL,
    CONFIRM_TOKEN,
    CONFIRMATION_REQUIRED_BATCH_COMMANDS,
    CONFIRMATION_REQUIRED_TOOLS,
    DANGEROUS_BATCH_COMMANDS,
    DANGEROUS_TOOLS,
    arm_dangerous_operations,
    check_armed_for_tool,
    check_confirmation,
    disarm_dangerous_operations,
    get_armed_time_remaining,
    is_armed,
)
from twincat_mcp.formatting import add_timing_to_output, format_duration
from twincat_mcp.cli import (
    find_tc_automation_exe,
    run_tc_automation,
    run_tc_automation_with_progress,
)
from twincat_mcp.host import (
    HOST_DISABLED,
    HostError,
    ShellHost,
    _ci_wrap,
    _CIDict,
    drop_shell_host,
    get_shell_host,
    get_shell_host_if_alive,
    shutdown_shell_host,
)
from twincat_mcp.dispatch import run_shell_step
from twincat_mcp.tools.schemas import get_tool_schemas

# Initialize MCP server.
server = Server("twincat-mcp")



@server.list_tools()
async def list_tools() -> list:
    """Advertise available TwinCAT tools (schemas live in twincat_mcp.tools.schemas)."""
    return get_tool_schemas()


@server.call_tool()
async def call_tool(name: str, arguments: dict) -> list[TextContent]:
    """Handle tool calls."""
    
    # Handle arm/disarm tool
    if name == "twincat_arm_dangerous_operations":
        disarm = arguments.get("disarm", False)
        reason = arguments.get("reason", "No reason provided")
        
        if disarm:
            result = disarm_dangerous_operations()
            output = "🔒 Dangerous operations DISARMED\n\nThe server is now in SAFE mode."
        else:
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
    
    # Check armed state for dangerous tools (pass arguments for context-aware checks)
    allowed, message = check_armed_for_tool(name, arguments)
    if not allowed:
        return [TextContent(type="text", text=message)]
    
    # Check confirmation for highly destructive tools
    confirmed, conf_message = check_confirmation(name, arguments)
    if not confirmed:
        return [TextContent(type="text", text=conf_message)]
    
    # Start timing for all tool operations
    tool_start_time = time.time()
    
    if name == "twincat_build":
        solution_path = arguments.get("solutionPath", "")
        clean = arguments.get("clean", True)
        tc_version = arguments.get("tcVersion")

        result, _ = run_shell_step(
            "build", {"clean": clean},
            solution_path=solution_path, tc_version=tc_version,
            timeout_minutes=10,
        )
        
        # Format output for the AI
        if result.get("success"):
            output = f"✅ {result.get('summary', 'Build succeeded')}\n"
            if result.get("warnings"):
                output += "\n⚠️ Warnings:\n"
                for w in result["warnings"]:
                    output += f"  - {w.get('fileName', '')}:{w.get('line', '')}: {w.get('description', '')}\n"
        else:
            output = f"❌ Build failed\n"
            if result.get("errorMessage"):
                error_msg = result['errorMessage']
                output += f"\nError: {error_msg}\n"
                # Detect RPC/COM errors caused by stale TcXaeShell instances
                if "0x800706BE" in error_msg or "RPC" in error_msg or "COM" in error_msg:
                    output += "\n💡 This error is likely caused by a stale TcXaeShell/devenv process holding locks on the solution.\n"
                    output += "   Use the `twincat_kill_stale` tool to kill stale TcXaeShell/devenv processes, then retry the build.\n"
            if result.get("errors"):
                output += "\n🔴 Errors:\n"
                for e in result["errors"]:
                    output += f"  - {e.get('fileName', '')}:{e.get('line', '')}: {e.get('description', '')}\n"
            if result.get("warnings"):
                output += "\n⚠️ Warnings:\n"
                for w in result["warnings"]:
                    output += f"  - {w.get('fileName', '')}:{w.get('line', '')}: {w.get('description', '')}\n"
        
        return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]
    
    elif name == "twincat_get_info":
        solution_path = arguments.get("solutionPath", "")
        tc_version = arguments.get("tcVersion")

        result, _ = run_shell_step(
            "info", {},
            solution_path=solution_path, tc_version=tc_version,
            timeout_minutes=5,
        )

        if result.get("errorMessage"):
            output = f"❌ Error: {result['errorMessage']}"
        else:
            output = f"""📋 TwinCAT Project Info
Solution: {result.get('solutionPath', 'Unknown')}
TwinCAT Version: {result.get('tcVersion', 'Unknown')} {'(pinned)' if result.get('tcVersionPinned') else ''}
Visual Studio Version: {result.get('visualStudioVersion', 'Unknown')}
Target Platform: {result.get('targetPlatform', 'Unknown')}

PLC Projects:
"""
            plcs = result.get("plcProjects", [])
            if plcs:
                for plc in plcs:
                    output += f"  - {plc.get('name', 'Unknown')} (AMS Port: {plc.get('amsPort', 'Unknown')})\n"
            else:
                output += "  (none found)\n"
        
        return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]
    
    elif name == "twincat_clean":
        solution_path = arguments.get("solutionPath", "")
        tc_version = arguments.get("tcVersion")

        result, _ = run_shell_step(
            "clean", {},
            solution_path=solution_path, tc_version=tc_version,
            timeout_minutes=5,
        )

        if result.get("success"):
            output = f"✅ {result.get('message', 'Solution cleaned successfully')}"
        else:
            output = f"❌ Clean failed: {result.get('error', 'Unknown error')}"
        
        return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]
    
    elif name == "twincat_set_target":
        solution_path = arguments.get("solutionPath", "")
        ams_net_id = arguments.get("amsNetId", "")
        tc_version = arguments.get("tcVersion")

        result, _ = run_shell_step(
            "set-target", {"amsNetId": ams_net_id},
            solution_path=solution_path, tc_version=tc_version,
            timeout_minutes=5,
        )
        
        if result.get("success"):
            output = f"✅ {result.get('message', 'Target set successfully')}\n"
            output += f"Previous target: {result.get('previousTarget', 'Unknown')}\n"
            output += f"New target: {result.get('newTarget', ams_net_id)}"
        else:
            output = f"❌ Set target failed: {result.get('error', 'Unknown error')}"
        
        return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]
    
    elif name == "twincat_activate":
        solution_path = arguments.get("solutionPath", "")
        ams_net_id = arguments.get("amsNetId")
        tc_version = arguments.get("tcVersion")

        step_args: dict = {}
        if ams_net_id:
            step_args["amsNetId"] = ams_net_id
        result, _ = run_shell_step(
            "activate", step_args,
            solution_path=solution_path, tc_version=tc_version,
            timeout_minutes=10,
        )
        
        if result.get("success"):
            output = f"✅ {result.get('message', 'Configuration activated')}\n"
            output += f"Target: {result.get('targetNetId', 'Unknown')}"
        else:
            output = f"❌ Activation failed: {result.get('error', 'Unknown error')}"
        
        return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]
    
    elif name == "twincat_restart":
        solution_path = arguments.get("solutionPath", "")
        ams_net_id = arguments.get("amsNetId")
        tc_version = arguments.get("tcVersion")

        step_args: dict = {}
        if ams_net_id:
            step_args["amsNetId"] = ams_net_id
        result, _ = run_shell_step(
            "restart", step_args,
            solution_path=solution_path, tc_version=tc_version,
            timeout_minutes=5,
        )
        
        if result.get("success"):
            output = f"✅ {result.get('message', 'TwinCAT restarted')}\n"
            output += f"Target: {result.get('targetNetId', 'Unknown')}"
        else:
            output = f"❌ Restart failed: {result.get('error', 'Unknown error')}"
        
        return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]
    
    elif name == "twincat_deploy":
        solution_path = arguments.get("solutionPath", "")
        ams_net_id = arguments.get("amsNetId", "")
        plc_name = arguments.get("plcName")
        tc_version = arguments.get("tcVersion")
        skip_build = arguments.get("skipBuild", False)
        dry_run = arguments.get("dryRun", False)

        step_args: dict = {"amsNetId": ams_net_id}
        if plc_name:
            step_args["plcName"] = plc_name
        if skip_build:
            step_args["skipBuild"] = True
        if dry_run:
            step_args["dryRun"] = True

        result, _ = run_shell_step(
            "deploy", step_args,
            solution_path=solution_path,
            tc_version=tc_version,
            timeout_minutes=20,
        )

        if result.get("success"):
            output = f"{'🔍 DRY RUN: ' if dry_run else ''}✅ {result.get('message', 'Deployment successful')}\n\n"
            output += f"Target: {result.get('targetNetId', ams_net_id)}\n"
            output += f"Deployed PLCs: {', '.join(result.get('deployedPlcs', []))}\n\n"
            
            if result.get("steps"):
                output += "📋 Deployment Steps:\n"
                for step in result["steps"]:
                    dry_note = " (dry run)" if step.get("dryRun") else ""
                    output += f"  {step.get('step', '?')}. {step.get('action', 'Unknown')}{dry_note}\n"
        else:
            output = f"❌ Deployment failed: {result.get('error', 'Unknown error')}\n"
            if result.get("errors"):
                output += "\n🔴 Build Errors:\n"
                for e in result["errors"]:
                    output += f"  - {e.get('file', '')}:{e.get('line', '')}: {e.get('description', '')}\n"
        
        return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]
    
    elif name == "twincat_list_plcs":
        solution_path = arguments.get("solutionPath", "")
        tc_version = arguments.get("tcVersion")

        result, _ = run_shell_step(
            "list-plcs", {},
            solution_path=solution_path, tc_version=tc_version,
            timeout_minutes=5,
        )
        
        if result.get("ErrorMessage"):
            output = f"❌ Error: {result['ErrorMessage']}"
        else:
            output = f"""📋 PLC Projects in Solution
Solution: {result.get('SolutionPath', 'Unknown')}
TwinCAT Version: {result.get('TcVersion', 'Unknown')}
PLC Count: {result.get('PlcCount', 0)}

"""
            plcs = result.get("PlcProjects", [])
            if plcs:
                for plc in plcs:
                    autostart = "✅" if plc.get("BootProjectAutostart") else "❌"
                    output += f"  {plc.get('Index', '?')}. {plc.get('Name', 'Unknown')}\n"
                    output += f"     AMS Port: {plc.get('AmsPort', 'Unknown')}\n"
                    output += f"     Boot Autostart: {autostart}\n"
                    if plc.get("Error"):
                        output += f"     ⚠️ {plc['Error']}\n"
                    output += "\n"
            else:
                output += "  (no PLC projects found)\n"
        
        return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]
    
    elif name == "twincat_set_boot_project":
        solution_path = arguments.get("solutionPath", "")
        plc_name = arguments.get("plcName")
        autostart = arguments.get("autostart", True)
        generate = arguments.get("generate", True)
        tc_version = arguments.get("tcVersion")

        step_args: dict = {"autostart": autostart, "generate": generate}
        if plc_name:
            step_args["plcName"] = plc_name
        result, _ = run_shell_step(
            "set-boot-project", step_args,
            solution_path=solution_path, tc_version=tc_version,
            timeout_minutes=10,
        )
        
        if result.get("Success"):
            output = f"✅ Boot project configuration updated\n\n"
            for plc in result.get("PlcResults", []):
                status = "✅" if plc.get("Success") else "❌"
                output += f"{status} {plc.get('Name', 'Unknown')}\n"
                output += f"   Autostart: {'enabled' if plc.get('AutostartEnabled') else 'disabled'}\n"
                output += f"   Boot Generated: {'yes' if plc.get('BootProjectGenerated') else 'no'}\n"
                if plc.get("Error"):
                    output += f"   ⚠️ {plc['Error']}\n"
        else:
            output = f"❌ Failed: {result.get('ErrorMessage', 'Unknown error')}"
        
        return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]
    
    elif name == "twincat_disable_io":
        solution_path = arguments.get("solutionPath", "")
        enable = arguments.get("enable", False)
        tc_version = arguments.get("tcVersion")

        result, _ = run_shell_step(
            "disable-io", {"enable": bool(enable)},
            solution_path=solution_path, tc_version=tc_version,
            timeout_minutes=5,
        )
        
        if result.get("Success"):
            action = "enabled" if enable else "disabled"
            modified = result.get('ModifiedCount', 0)
            total = result.get('TotalDevices', 0)
            
            if modified > 0:
                output = f"✅ {modified} device(s) {action}\n\n"
            else:
                output = f"✅ All {total} device(s) already {action} (no changes needed)\n\n"
            
            output += f"📊 Total devices: {total}\n"
            
            devices = result.get("Devices", [])
            if devices:
                output += "📋 Device Status:\n"
                for dev in devices:
                    modified = "🔄" if dev.get("Modified") else "—"
                    output += f"  {modified} {dev.get('Name', 'Unknown')}: {dev.get('CurrentState', 'Unknown')}\n"
                    if dev.get("Error"):
                        output += f"     ⚠️ {dev['Error']}\n"
        else:
            output = f"❌ Failed: {result.get('ErrorMessage', 'Unknown error')}"
        
        return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]
    
    elif name == "twincat_set_variant":
        solution_path = arguments.get("solutionPath", "")
        variant_name = arguments.get("variantName")
        tc_version = arguments.get("tcVersion")

        step_args: dict = {}
        if variant_name:
            step_args["variantName"] = variant_name
        else:
            step_args["getOnly"] = True
        result, _ = run_shell_step(
            "set-variant", step_args,
            solution_path=solution_path, tc_version=tc_version,
            timeout_minutes=5,
        )
        
        if result.get("Success"):
            output = f"✅ {result.get('Message', 'Variant operation successful')}\n\n"
            output += f"Previous variant: {result.get('PreviousVariant') or '(default)'}\n"
            output += f"Current variant: {result.get('CurrentVariant') or '(default)'}"
        else:
            output = f"❌ Failed: {result.get('ErrorMessage', 'Unknown error')}"
        
        return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]
    
    # Phase 4: ADS Communication Tools
    # Note: C# outputs PascalCase JSON keys (Success, AdsState, etc.)
    elif name == "twincat_get_state":
        ams_net_id = arguments.get("amsNetId", "")
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
    
    elif name == "twincat_set_state":
        ams_net_id = arguments.get("amsNetId", "")
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
    
    elif name == "twincat_read_var":
        ams_net_id = arguments.get("amsNetId", "")
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
    
    elif name == "twincat_write_var":
        ams_net_id = arguments.get("amsNetId", "")
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
    
    # Phase 4: Task Management Tools
    elif name == "twincat_list_tasks":
        solution_path = arguments.get("solutionPath", "")
        tc_version = arguments.get("tcVersion")

        result, _ = run_shell_step(
            "list-tasks", {},
            solution_path=solution_path, tc_version=tc_version,
            timeout_minutes=5,
        )
        
        if result.get("Success"):
            tasks = result.get("Tasks", [])
            output = f"📋 Real-Time Tasks ({len(tasks)} found)\n\n"
            for task in tasks:
                # C# outputs Disabled (inverted), so enabled = not Disabled
                enabled = "✅" if not task.get("Disabled", True) else "❌"
                autostart = "🚀" if task.get("AutoStart", False) else "⏸️"
                cycle_us = task.get("CycleTimeUs", 0)
                cycle_ms = cycle_us / 1000 if cycle_us else 0
                output += f"{enabled} **{task.get('Name', 'Unknown')}**\n"
                output += f"   Priority: {task.get('Priority', '-')}\n"
                output += f"   Cycle Time: {cycle_ms}ms ({cycle_us}µs)\n"
                output += f"   Autostart: {autostart} {'Yes' if task.get('AutoStart') else 'No'}\n\n"
        else:
            output = f"❌ Failed: {result.get('ErrorMessage', 'Unknown error')}"
        
        return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]
    
    elif name == "twincat_configure_task":
        solution_path = arguments.get("solutionPath", "")
        task_name = arguments.get("taskName", "")
        enable = arguments.get("enable")
        autostart = arguments.get("autostart")
        tc_version = arguments.get("tcVersion")

        step_args: dict = {"taskName": task_name}
        if enable is not None:
            step_args["enable"] = bool(enable)
        if autostart is not None:
            step_args["autoStart"] = bool(autostart)
        result, _ = run_shell_step(
            "configure-task", step_args,
            solution_path=solution_path, tc_version=tc_version,
            timeout_minutes=5,
        )
        
        if result.get("Success"):
            output = f"✅ Task '{task_name}' configured\n\n"
            output += f"Enabled: {'Yes' if result.get('Enabled') else 'No'}\n"
            output += f"Autostart: {'Yes' if result.get('AutoStart') else 'No'}"
        else:
            output = f"❌ Failed to configure '{task_name}': {result.get('ErrorMessage', 'Unknown error')}"
        
        return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]
    
    elif name == "twincat_configure_rt":
        solution_path = arguments.get("solutionPath", "")
        max_cpus = arguments.get("maxCpus")
        load_limit = arguments.get("loadLimit")
        tc_version = arguments.get("tcVersion")

        step_args: dict = {}
        if max_cpus is not None:
            step_args["maxCpus"] = int(max_cpus)
        if load_limit is not None:
            step_args["loadLimit"] = int(load_limit)
        result, _ = run_shell_step(
            "configure-rt", step_args,
            solution_path=solution_path, tc_version=tc_version,
            timeout_minutes=5,
        )
        
        if result.get("Success"):
            output = f"✅ Real-Time Settings Configured\n\n"
            output += f"🖥️ Max Isolated CPU Cores: {result.get('MaxCpus', '-')}\n"
            output += f"📊 CPU Load Limit: {result.get('LoadLimit', '-')}%"
        else:
            output = f"❌ Failed: {result.get('ErrorMessage', 'Unknown error')}"
        
        return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]
    
    # Code Analysis Tools
    elif name == "twincat_check_all_objects":
        solution_path = arguments.get("solutionPath", "")
        plc_name = arguments.get("plcName")
        tc_version = arguments.get("tcVersion")

        step_args: dict = {}
        if plc_name:
            step_args["plcName"] = plc_name
        result, _ = run_shell_step(
            "check-all-objects", step_args,
            solution_path=solution_path, tc_version=tc_version,
            timeout_minutes=15,
        )
        
        if result.get("success"):
            output = f"✅ {result.get('message', 'Check completed')}\n\n"
            
            # Show PLC results
            for plc in result.get("plcResults", []):
                status = "✅" if plc.get("success") else "❌"
                output += f"{status} {plc.get('name', 'Unknown')}\n"
                if plc.get("error"):
                    output += f"   ⚠️ {plc['error']}\n"
            
            # Show warnings if any
            warnings = result.get("warnings", [])
            if warnings:
                output += f"\n⚠️ Warnings ({len(warnings)}):\n"
                for w in warnings[:10]:  # Limit to first 10
                    output += f"  • {w.get('fileName', '')}:{w.get('line', '')}: {w.get('description', '')}\n"
                if len(warnings) > 10:
                    output += f"  ... and {len(warnings) - 10} more\n"
        else:
            output = f"❌ Check all objects failed\n\n"
            if result.get("errorMessage"):
                output += f"Error: {result['errorMessage']}\n"
            
            # Show errors
            errors = result.get("errors", [])
            if errors:
                output += f"\n🔴 Errors ({len(errors)}):\n"
                for e in errors[:15]:  # Limit to first 15
                    output += f"  • {e.get('fileName', '')}:{e.get('line', '')}: {e.get('description', '')}\n"
                if len(errors) > 15:
                    output += f"  ... and {len(errors) - 15} more\n"
            
            # Show warnings
            warnings = result.get("warnings", [])
            if warnings:
                output += f"\n⚠️ Warnings ({len(warnings)}):\n"
                for w in warnings[:10]:
                    output += f"  • {w.get('fileName', '')}:{w.get('line', '')}: {w.get('description', '')}\n"
                if len(warnings) > 10:
                    output += f"  ... and {len(warnings) - 10} more\n"
        
        return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]
    
    elif name == "twincat_static_analysis":
        solution_path = arguments.get("solutionPath", "")
        check_all = arguments.get("checkAll", True)
        plc_name = arguments.get("plcName")
        tc_version = arguments.get("tcVersion")

        step_args: dict = {"checkAll": bool(check_all)}
        if plc_name:
            step_args["plcName"] = plc_name
        result, _ = run_shell_step(
            "static-analysis", step_args,
            solution_path=solution_path, tc_version=tc_version,
            timeout_minutes=15,
        )
        
        if result.get("success"):
            scope = "all objects" if result.get("checkedAllObjects") else "used objects"
            output = f"✅ Static Analysis Complete ({scope})\n\n"
            output += f"📊 {result.get('errorCount', 0)} error(s), {result.get('warningCount', 0)} warning(s)\n\n"
            
            # Show PLC results
            for plc in result.get("plcResults", []):
                status = "✅" if plc.get("success") else "❌"
                output += f"{status} {plc.get('name', 'Unknown')}\n"
                if plc.get("error"):
                    output += f"   ⚠️ {plc['error']}\n"
            
            # Show errors
            errors = result.get("errors", [])
            if errors:
                output += f"\n🔴 Errors:\n"
                for e in errors[:10]:
                    rule = f"[{e.get('ruleId')}] " if e.get('ruleId') else ""
                    output += f"  • {rule}{e.get('fileName', '')}:{e.get('line', '')}: {e.get('description', '')}\n"
                if len(errors) > 10:
                    output += f"  ... and {len(errors) - 10} more\n"
            
            # Show warnings
            warnings = result.get("warnings", [])
            if warnings:
                output += f"\n⚠️ Warnings:\n"
                for w in warnings[:10]:
                    rule = f"[{w.get('ruleId')}] " if w.get('ruleId') else ""
                    output += f"  • {rule}{w.get('fileName', '')}:{w.get('line', '')}: {w.get('description', '')}\n"
                if len(warnings) > 10:
                    output += f"  ... and {len(warnings) - 10} more\n"
        else:
            output = f"❌ Static Analysis Failed\n\n"
            if result.get("errorMessage"):
                output += f"Error: {result['errorMessage']}\n"
                if "TE1200" in result.get("errorMessage", "") or "license" in result.get("errorMessage", "").lower():
                    output += "\n💡 Tip: Static Analysis requires the TE1200 license from Beckhoff."
        
        return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]

    elif name == "twincat_generate_library":
        solution_path = arguments.get("solutionPath", "")
        plc_name = arguments.get("plcName", "")
        library_location = arguments.get("libraryLocation")
        tc_version = arguments.get("tcVersion")
        skip_build = arguments.get("skipBuild", False)
        dry_run = arguments.get("dryRun", False)

        step_args: dict = {
            "plcName": plc_name,
            "skipBuild": bool(skip_build),
            "dryRun": bool(dry_run),
        }
        if library_location:
            step_args["libraryLocation"] = library_location
        result, _ = run_shell_step(
            "generate-library", step_args,
            solution_path=solution_path, tc_version=tc_version,
            timeout_minutes=15,
        )

        if result.get("success"):
            status_prefix = "🔍 DRY RUN: " if result.get("dryRun") else "✅ "
            output = f"{status_prefix}{result.get('message', 'Library generated successfully')}\n\n"
            output += f"PLC: {result.get('plcName', plc_name)}\n"
            output += f"Output: {result.get('outputLibraryPath', 'Unknown')}\n"
            output += f"Build Skipped: {'Yes' if result.get('buildSkipped') else 'No'}"
        else:
            output = "❌ Library generation failed\n\n"
            if result.get("errorMessage"):
                output += f"Error: {result.get('errorMessage')}\n"
            if result.get("outputLibraryPath"):
                output += f"Resolved Output Path: {result.get('outputLibraryPath')}\n"

        return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]
    
    elif name == "twincat_list_routes":
        # List ADS routes from TwinCAT StaticRoutes.xml
        import xml.etree.ElementTree as ET
        
        # Find StaticRoutes.xml
        routes_file = None
        
        # Try TWINCAT3DIR environment variable first
        tc_dir = os.environ.get("TWINCAT3DIR", "")
        if tc_dir:
            candidate = Path(tc_dir).parent / "3.1" / "Target" / "StaticRoutes.xml"
            if candidate.exists():
                routes_file = candidate
        
        # Try common install locations
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
            
            # Find all Route elements
            routes = []
            for route in root.findall(".//Route"):
                name = route.find("Name")
                address = route.find("Address")
                netid = route.find("NetId")
                
                if name is not None and netid is not None:
                    routes.append({
                        "name": name.text or "",
                        "address": address.text if address is not None else "",
                        "amsNetId": netid.text or ""
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
    
    elif name == "twincat_get_error_list":
        solution_path = arguments.get("solutionPath", "")
        tc_version = arguments.get("tcVersion")
        include_messages = arguments.get("includeMessages", True)
        include_warnings = arguments.get("includeWarnings", True)
        include_errors = arguments.get("includeErrors", True)
        wait_seconds = arguments.get("waitSeconds", 0)

        step_args = {
            "includeMessages": bool(include_messages),
            "includeWarnings": bool(include_warnings),
            "includeErrors": bool(include_errors),
            "waitSeconds": int(wait_seconds),
        }
        result, _ = run_shell_step(
            "get-error-list", step_args,
            solution_path=solution_path, tc_version=tc_version,
            timeout_minutes=5,
        )
        
        if result.get("success"):
            error_count = result.get("errorCount", 0)
            warning_count = result.get("warningCount", 0)
            message_count = result.get("messageCount", 0)
            total = result.get("totalCount", 0)
            
            output = f"📋 Error List ({total} items)\n\n"
            output += f"🔴 Errors: {error_count} | 🟡 Warnings: {warning_count} | 💬 Messages: {message_count}\n\n"
            
            items = result.get("items", [])
            if items:
                for item in items:
                    level = item.get("level", "")
                    desc = item.get("description", "")
                    filename = item.get("fileName", "")
                    line = item.get("line", 0)
                    
                    if level == "Error":
                        icon = "🔴"
                    elif level == "Warning":
                        icon = "🟡"
                    else:
                        icon = "💬"
                    
                    if filename and line > 0:
                        output += f"{icon} {filename}:{line} - {desc}\n"
                    else:
                        output += f"{icon} {desc}\n"
            else:
                output += "No items in error list."
        else:
            output = f"❌ Failed to read error list: {result.get('errorMessage', 'Unknown error')}"
        
        return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]
    
    elif name == "twincat_run_tcunit":
        solution_path = arguments.get("solutionPath", "")
        ams_net_id = arguments.get("amsNetId")
        task_name = arguments.get("taskName")
        plc_name = arguments.get("plcName")
        tc_version = arguments.get("tcVersion")
        timeout_minutes = arguments.get("timeoutMinutes", 10)
        disable_io = arguments.get("disableIo", False)
        skip_build = arguments.get("skipBuild", False)

        step_args: dict = {"timeoutMinutes": timeout_minutes}
        if ams_net_id:
            step_args["amsNetId"] = ams_net_id
        if task_name:
            step_args["taskName"] = task_name
        if plc_name:
            step_args["plcName"] = plc_name
        if disable_io:
            step_args["disableIo"] = True
        if skip_build:
            step_args["skipBuild"] = True

        result, progress_messages = run_shell_step(
            "run-tcunit", step_args,
            solution_path=solution_path,
            tc_version=tc_version,
            timeout_minutes=timeout_minutes,
        )
        
        # Build output with progress section
        output = "🧪 TcUnit Test Run\n\n"
        
        # Show execution progress
        if progress_messages:
            output += "📋 Execution Log:\n"
            for msg in progress_messages:
                # Add step icons based on content
                if "error" in msg.lower() or "failed" in msg.lower():
                    output += f"  ❌ {msg}\n"
                elif "succeeded" in msg.lower() or "passed" in msg.lower() or "completed" in msg.lower():
                    output += f"  ✅ {msg}\n"
                elif "waiting" in msg.lower() or "polling" in msg.lower():
                    output += f"  ⏳ {msg}\n"
                elif "starting" in msg.lower() or "opening" in msg.lower() or "loading" in msg.lower():
                    output += f"  🔄 {msg}\n"
                elif "building" in msg.lower() or "cleaning" in msg.lower():
                    output += f"  🔨 {msg}\n"
                elif "configuring" in msg.lower() or "configured" in msg.lower():
                    output += f"  ⚙️ {msg}\n"
                elif "activating" in msg.lower() or "activated" in msg.lower():
                    output += f"  📤 {msg}\n"
                elif "restarting" in msg.lower() or "restart" in msg.lower():
                    output += f"  🔄 {msg}\n"
                elif "disabling" in msg.lower() or "disabled" in msg.lower():
                    output += f"  🚫 {msg}\n"
                else:
                    output += f"  ▸ {msg}\n"
            output += "\n"
        
        if result.get("success"):
            total_tests = result.get("totalTests", 0)
            passed = result.get("passedTests", 0)
            failed = result.get("failedTests", 0)
            test_suites = result.get("testSuites", 0)
            duration = result.get("duration", 0)
            
            # Determine overall status
            if failed > 0:
                status = "❌ TESTS FAILED"
            elif total_tests > 0:
                status = "✅ ALL TESTS PASSED"
            else:
                status = "⚠️ NO TESTS FOUND"
            
            output += f"{'='*40}\n"
            output += f"{status}\n"
            output += f"{'='*40}\n\n"
            
            output += f"📊 Summary:\n"
            output += f"  • Test Suites: {test_suites}\n"
            output += f"  • Total Tests: {total_tests}\n"
            output += f"  • ✅ Passed: {passed}\n"
            output += f"  • ❌ Failed: {failed}\n"
            if duration:
                output += f"  • Duration: {duration:.1f}s\n"
            
            # Show failed test details only (not passed tests)
            failed_details = result.get("failedTestDetails", [])
            if failed_details:
                output += f"\n🔴 Failed Tests ({len(failed_details)}):\n"
                for detail in failed_details:
                    # Clean up the detail message for readability
                    output += f"  • {detail}\n"
            elif failed > 0:
                # We know there are failures but didn't capture details
                output += f"\n🔴 {failed} test(s) failed - check TcUnit output for details\n"
            
            # Only show summary line count, not all messages
            test_messages = result.get("testMessages", [])
            if test_messages and failed == 0:
                output += f"\n✅ All {total_tests} tests passed (detailed log available with {len(test_messages)} messages)\n"
        else:
            error_msg = result.get("errorMessage", "Unknown error")
            output += f"{'='*40}\n"
            output += f"❌ TEST RUN FAILED\n"
            output += f"{'='*40}\n\n"
            output += f"Error: {error_msg}\n"
            
            # Show any test messages collected before failure
            test_messages = result.get("testMessages", [])
            if test_messages:
                output += f"\n💬 Messages before failure:\n"
                for msg in test_messages:
                    output += f"  {msg}\n"
            # Show any error details
            build_errors = result.get("buildErrors", [])
            if build_errors:
                output += f"\n\n🔴 Build Errors:\n"
                for err in build_errors:
                    output += f"  • {err}\n"
        
        return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]
    
    elif name == "twincat_kill_stale":
        # SURGICAL cleanup: NEVER kill TcXaeShell / devenv by image name.
        # Doing so closes the user's interactive IDE, their Visual Studio, and
        # every other automation's shell on the box.
        #
        # Safety model (defense in depth):
        #   1) Kill our own persistent shell host + its DTE (we own those PIDs).
        #   2) Run the janitor for session files from *crashed* MCP instances.
        #      Those are guaranteed-dead MCPs; their hosts/DTEs are orphans.
        #   3) As a last-resort, sweep TcXaeShell instances that LOOK headless
        #      (no main window title). User-opened IDEs always have a title.
        output_parts: list[str] = []
        killed_own_host = False
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
                killed_own_host = True
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
                f"🔪 Shut down our own shell host"
                + (f" (DTE PID {killed_own_dte_pid})" if killed_own_dte_pid else "")
            )

        # Run the C# janitor explicitly via `reap-orphans`. It walks the session
        # files from crashed MCPs and safely kills matching orphans (start-time
        # verified, so a reused PID never hits the wrong process).
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

        # NOTE: No "headless sweep" by MainWindowTitle. That heuristic is
        # unsafe — a user-opened TcXaeShell can legitimately report an empty
        # title during startup or when a modal dialog is active, and we would
        # kill their IDE. If reap-orphans couldn't identify it via a session
        # file, it isn't ours.
        if not output_parts:
            output_parts.append("✅ Nothing to clean up. Your Visual Studio / TcXaeShell sessions were not touched.")
        else:
            output_parts.append(
                "\nℹ️ This tool never kills `TcXaeShell.exe` or `devenv.exe` by image name — "
                "your open IDE is safe."
            )

        return [TextContent(type="text", text=add_timing_to_output("\n".join(output_parts), tool_start_time))]

    elif name == "twincat_host_status":
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
    
    elif name == "twincat_batch":
        solution_path = arguments.get("solutionPath", "") or ""
        tc_version = arguments.get("tcVersion")
        stop_on_error = arguments.get("stopOnError", True)
        timeout_minutes = int(arguments.get("timeoutMinutes", 15))
        steps = arguments.get("steps", []) or []
        confirm = arguments.get("confirm", "")
        
        if not isinstance(steps, list) or len(steps) == 0:
            return [TextContent(type="text", text="❌ twincat_batch requires a non-empty 'steps' list.")]
        
        # Validate each step and collect the set of commands used so we can do
        # batch-aware safety checks.
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
        
        # Batch-aware confirmation check: activate/restart inside a batch still
        # need an explicit 'CONFIRM'.
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
                    "args": step.get("args", {}) or {}
                }
                for step in steps
            ]
        }
        if solution_path:
            batch_input["solutionPath"] = solution_path
        if tc_version:
            batch_input["tcVersion"] = tc_version
        
        # Write to a temp file (safer than stdin for larger batches and keeps
        # the JSON easy to inspect on failure).
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
        
        # Header
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
        
        # Progress log
        if progress_messages:
            output += "\n📋 Execution Log:\n"
            for msg in progress_messages:
                output += f"  ▸ {msg}\n"
        
        # Per-step summary + detail
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
                #
                # NB: BatchCommand re-serializes shell-step results with
                # JsonNamingPolicy.CamelCase, but ADS steps are captured from
                # the original stdout which is PascalCase. So we use case-
                # insensitive lookups to handle both.
                if isinstance(inner, dict):
                    def _g(d: dict, *names, default=None):
                        """Case-insensitive get; returns first match among names."""
                        if not isinstance(d, dict):
                            return default
                        lowered = {k.lower(): k for k in d.keys()}
                        for n in names:
                            key = lowered.get(n.lower())
                            if key is not None:
                                return d[key]
                        return default
                    
                    if cmd == "build":
                        errs = _g(inner, "errors") or []
                        warns = _g(inner, "warnings") or []
                        if errs:
                            output += f"      🔴 {len(errs)} error(s):\n"
                            for e in errs[:5]:
                                output += f"         - {_g(e,'fileName','file','')}:{_g(e,'line','')}: {_g(e,'description','message','')}\n"
                            if len(errs) > 5:
                                output += f"         ... and {len(errs) - 5} more\n"
                        if warns:
                            output += f"      🟡 {len(warns)} warning(s)\n"
                    elif cmd == "info":
                        output += (
                            f"      TwinCAT: {_g(inner,'tcVersion', default='?')} | "
                            f"Platform: {_g(inner,'targetPlatform', default='?')}\n"
                        )
                        for plc in _g(inner, "plcProjects") or []:
                            output += f"        - {_g(plc,'name', default='?')} (port {_g(plc,'amsPort', default='?')})\n"
                    elif cmd == "list-plcs":
                        for plc in _g(inner, "plcProjects") or []:
                            output += (
                                f"        - {_g(plc,'name', default='?')} "
                                f"(port {_g(plc,'amsPort', default='?')}, "
                                f"boot={_g(plc,'bootProjectAutostart')})\n"
                            )
                    elif cmd == "list-tasks":
                        for t in _g(inner, "tasks") or []:
                            cycle_us = _g(t, "cycleTimeUs", default=0)
                            output += (
                                f"        - {_g(t,'name', default='?')}  "
                                f"cycle={cycle_us}µs  "
                                f"enabled={not _g(t,'disabled', default=True)}  "
                                f"autostart={_g(t,'autoStart', default=False)}\n"
                            )
                    elif cmd == "get-state":
                        output += (
                            f"      State: {_g(inner,'adsState', default='?')}  "
                            f"({_g(inner,'stateDescription', default='')})\n"
                        )
                    elif cmd == "read-var":
                        output += (
                            f"      {_g(inner,'dataType', default='?')}  "
                            f"value=`{_g(inner,'value')}`\n"
                        )
                    elif cmd == "write-var":
                        output += (
                            f"      {_g(inner,'dataType', default='?')}  "
                            f"prev=`{_g(inner,'previousValue')}`  "
                            f"new=`{_g(inner,'newValue')}`\n"
                        )
                    elif cmd == "set-variant":
                        output += (
                            f"      variant: {_g(inner,'previousVariant') or '(default)'} "
                            f"-> {_g(inner,'currentVariant') or '(default)'}\n"
                        )
                    elif cmd == "generate-library":
                        out_path = _g(inner, "outputLibraryPath")
                        if out_path:
                            output += f"      output: {out_path}\n"
        
        # Top-level error from C# (e.g. couldn't open shell)
        if not overall_success and result.get("errorMessage"):
            output += f"\n💥 Error: {result['errorMessage']}\n"
            err_msg = str(result.get("errorMessage", ""))
            if "0x800706BE" in err_msg or "RPC" in err_msg or " COM" in err_msg:
                output += (
                    "\n💡 This looks like a stale TcXaeShell/devenv holding COM locks.\n"
                    "   Run `twincat_kill_stale` and retry this batch.\n"
                )
        
        return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]
    
    else:
        return [TextContent(type="text", text=f"Unknown tool: {name}")]


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
