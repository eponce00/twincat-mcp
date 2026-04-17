"""
Shell-routed handlers: every tool that needs a TwinCAT/DTE session and
goes through `run_shell_step` (persistent host when available, CLI
fallback otherwise).

Grouped here by their shared dispatch shape. Keeping them together makes
it obvious what the "normal" tool pattern looks like; the specialty
handlers (deploy, tcunit, batch, ADS, safety) live in their own modules.

Handlers covered:
  twincat_build                twincat_get_info           twincat_clean
  twincat_set_target           twincat_activate           twincat_restart
  twincat_list_plcs            twincat_set_boot_project   twincat_disable_io
  twincat_set_variant          twincat_list_tasks         twincat_configure_task
  twincat_configure_rt         twincat_check_all_objects  twincat_static_analysis
  twincat_generate_library     twincat_get_error_list
"""

from mcp.types import TextContent

from ..defaults import resolve_ams_net_id
from ..dispatch import run_shell_step
from ..formatting import add_timing_to_output
from ._registry import register


@register("twincat_build")
async def handle_build(arguments: dict, tool_start_time: float) -> list[TextContent]:
    solution_path = arguments.get("solutionPath", "")
    clean = arguments.get("clean", True)
    tc_version = arguments.get("tcVersion")

    result, _ = run_shell_step(
        "build", {"clean": clean},
        solution_path=solution_path, tc_version=tc_version,
        timeout_minutes=10,
    )

    if result.get("success"):
        output = f"✅ {result.get('summary', 'Build succeeded')}\n"
        if result.get("warnings"):
            output += "\n⚠️ Warnings:\n"
            for w in result["warnings"]:
                output += f"  - {w.get('fileName', '')}:{w.get('line', '')}: {w.get('description', '')}\n"
    else:
        # Prefer BuildResult.summary ("Build failed with N error(s) and M
        # warning(s) in X.Ys") when present — it gives the counts
        # up-front. Fall back to errorMessage for catastrophic failures
        # (solution not found, RPC/COM error, etc.) where BuildCommand
        # never reached the compile step.
        summary = result.get("summary")
        error_msg = result.get("errorMessage")
        generic_fallbacks = {"Step failed", "Batch failed", "Batch dispatch failed"}

        if summary:
            output = f"❌ {summary}\n"
        else:
            output = "❌ Build failed\n"

        if error_msg and error_msg not in generic_fallbacks:
            output += f"\nError: {error_msg}\n"
            # Detect RPC/COM errors caused by stale TcXaeShell instances.
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


@register("twincat_get_info")
async def handle_get_info(arguments: dict, tool_start_time: float) -> list[TextContent]:
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


@register("twincat_clean")
async def handle_clean(arguments: dict, tool_start_time: float) -> list[TextContent]:
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


@register("twincat_set_target")
async def handle_set_target(arguments: dict, tool_start_time: float) -> list[TextContent]:
    solution_path = arguments.get("solutionPath", "")
    ams_net_id = resolve_ams_net_id(arguments.get("amsNetId"))
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


@register("twincat_activate")
async def handle_activate(arguments: dict, tool_start_time: float) -> list[TextContent]:
    solution_path = arguments.get("solutionPath", "")
    ams_net_id = resolve_ams_net_id(arguments.get("amsNetId"))
    tc_version = arguments.get("tcVersion")

    result, _ = run_shell_step(
        "activate", {"amsNetId": ams_net_id},
        solution_path=solution_path, tc_version=tc_version,
        timeout_minutes=10,
    )

    if result.get("success"):
        output = f"✅ {result.get('message', 'Configuration activated')}\n"
        output += f"Target: {result.get('targetNetId', 'Unknown')}"
    else:
        output = f"❌ Activation failed: {result.get('error', 'Unknown error')}"

    return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]


@register("twincat_restart")
async def handle_restart(arguments: dict, tool_start_time: float) -> list[TextContent]:
    solution_path = arguments.get("solutionPath", "")
    ams_net_id = resolve_ams_net_id(arguments.get("amsNetId"))
    tc_version = arguments.get("tcVersion")

    result, _ = run_shell_step(
        "restart", {"amsNetId": ams_net_id},
        solution_path=solution_path, tc_version=tc_version,
        timeout_minutes=5,
    )

    if result.get("success"):
        output = f"✅ {result.get('message', 'TwinCAT restarted')}\n"
        output += f"Target: {result.get('targetNetId', 'Unknown')}"
    else:
        output = f"❌ Restart failed: {result.get('error', 'Unknown error')}"

    return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]


@register("twincat_list_plcs")
async def handle_list_plcs(arguments: dict, tool_start_time: float) -> list[TextContent]:
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


@register("twincat_set_boot_project")
async def handle_set_boot_project(arguments: dict, tool_start_time: float) -> list[TextContent]:
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
        output = "✅ Boot project configuration updated\n\n"
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


@register("twincat_disable_io")
async def handle_disable_io(arguments: dict, tool_start_time: float) -> list[TextContent]:
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
                modified_icon = "🔄" if dev.get("Modified") else "—"
                output += f"  {modified_icon} {dev.get('Name', 'Unknown')}: {dev.get('CurrentState', 'Unknown')}\n"
                if dev.get("Error"):
                    output += f"     ⚠️ {dev['Error']}\n"
    else:
        output = f"❌ Failed: {result.get('ErrorMessage', 'Unknown error')}"

    return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]


@register("twincat_set_variant")
async def handle_set_variant(arguments: dict, tool_start_time: float) -> list[TextContent]:
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


@register("twincat_list_tasks")
async def handle_list_tasks(arguments: dict, tool_start_time: float) -> list[TextContent]:
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


@register("twincat_configure_task")
async def handle_configure_task(arguments: dict, tool_start_time: float) -> list[TextContent]:
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


@register("twincat_configure_rt")
async def handle_configure_rt(arguments: dict, tool_start_time: float) -> list[TextContent]:
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
        output = "✅ Real-Time Settings Configured\n\n"
        output += f"🖥️ Max Isolated CPU Cores: {result.get('MaxCpus', '-')}\n"
        output += f"📊 CPU Load Limit: {result.get('LoadLimit', '-')}%"
    else:
        output = f"❌ Failed: {result.get('ErrorMessage', 'Unknown error')}"

    return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]


@register("twincat_check_all_objects")
async def handle_check_all_objects(arguments: dict, tool_start_time: float) -> list[TextContent]:
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

        for plc in result.get("plcResults", []):
            status = "✅" if plc.get("success") else "❌"
            output += f"{status} {plc.get('name', 'Unknown')}\n"
            if plc.get("error"):
                output += f"   ⚠️ {plc['error']}\n"

        warnings = result.get("warnings", [])
        if warnings:
            output += f"\n⚠️ Warnings ({len(warnings)}):\n"
            for w in warnings[:10]:
                output += f"  • {w.get('fileName', '')}:{w.get('line', '')}: {w.get('description', '')}\n"
            if len(warnings) > 10:
                output += f"  ... and {len(warnings) - 10} more\n"
    else:
        output = "❌ Check all objects failed\n\n"
        if result.get("errorMessage"):
            output += f"Error: {result['errorMessage']}\n"

        errors = result.get("errors", [])
        if errors:
            output += f"\n🔴 Errors ({len(errors)}):\n"
            for e in errors[:15]:
                output += f"  • {e.get('fileName', '')}:{e.get('line', '')}: {e.get('description', '')}\n"
            if len(errors) > 15:
                output += f"  ... and {len(errors) - 15} more\n"

        warnings = result.get("warnings", [])
        if warnings:
            output += f"\n⚠️ Warnings ({len(warnings)}):\n"
            for w in warnings[:10]:
                output += f"  • {w.get('fileName', '')}:{w.get('line', '')}: {w.get('description', '')}\n"
            if len(warnings) > 10:
                output += f"  ... and {len(warnings) - 10} more\n"

    return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]


@register("twincat_static_analysis")
async def handle_static_analysis(arguments: dict, tool_start_time: float) -> list[TextContent]:
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

        for plc in result.get("plcResults", []):
            status = "✅" if plc.get("success") else "❌"
            output += f"{status} {plc.get('name', 'Unknown')}\n"
            if plc.get("error"):
                output += f"   ⚠️ {plc['error']}\n"

        errors = result.get("errors", [])
        if errors:
            output += "\n🔴 Errors:\n"
            for e in errors[:10]:
                rule = f"[{e.get('ruleId')}] " if e.get('ruleId') else ""
                output += f"  • {rule}{e.get('fileName', '')}:{e.get('line', '')}: {e.get('description', '')}\n"
            if len(errors) > 10:
                output += f"  ... and {len(errors) - 10} more\n"

        warnings = result.get("warnings", [])
        if warnings:
            output += "\n⚠️ Warnings:\n"
            for w in warnings[:10]:
                rule = f"[{w.get('ruleId')}] " if w.get('ruleId') else ""
                output += f"  • {rule}{w.get('fileName', '')}:{w.get('line', '')}: {w.get('description', '')}\n"
            if len(warnings) > 10:
                output += f"  ... and {len(warnings) - 10} more\n"
    else:
        output = "❌ Static Analysis Failed\n\n"
        if result.get("errorMessage"):
            output += f"Error: {result['errorMessage']}\n"
            if "TE1200" in result.get("errorMessage", "") or "license" in result.get("errorMessage", "").lower():
                output += "\n💡 Tip: Static Analysis requires the TE1200 license from Beckhoff."

    return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]


@register("twincat_generate_library")
async def handle_generate_library(arguments: dict, tool_start_time: float) -> list[TextContent]:
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


@register("twincat_get_error_list")
async def handle_get_error_list(arguments: dict, tool_start_time: float) -> list[TextContent]:
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
