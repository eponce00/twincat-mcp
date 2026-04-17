"""
TcUnit test-runner handler.

Unlike the other shell-routed tools this one consumes the progress
stream from `run_shell_step` to render a live execution log, then
renders a test summary with pass/fail breakdown. The output formatter
is the bulk of the file.
"""

from mcp.types import TextContent

from ..dispatch import run_shell_step
from ..formatting import add_timing_to_output
from ._registry import register


@register("twincat_run_tcunit")
async def handle_run_tcunit(arguments: dict, tool_start_time: float) -> list[TextContent]:
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

    output = "🧪 TcUnit Test Run\n\n"

    # Render the progress log with per-phase icons.
    if progress_messages:
        output += "📋 Execution Log:\n"
        for msg in progress_messages:
            low = msg.lower()
            if "error" in low or "failed" in low:
                output += f"  ❌ {msg}\n"
            elif "succeeded" in low or "passed" in low or "completed" in low:
                output += f"  ✅ {msg}\n"
            elif "waiting" in low or "polling" in low:
                output += f"  ⏳ {msg}\n"
            elif "starting" in low or "opening" in low or "loading" in low:
                output += f"  🔄 {msg}\n"
            elif "building" in low or "cleaning" in low:
                output += f"  🔨 {msg}\n"
            elif "configuring" in low or "configured" in low:
                output += f"  ⚙️ {msg}\n"
            elif "activating" in low or "activated" in low:
                output += f"  📤 {msg}\n"
            elif "restarting" in low or "restart" in low:
                output += f"  🔄 {msg}\n"
            elif "disabling" in low or "disabled" in low:
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

        if failed > 0:
            status = "❌ TESTS FAILED"
        elif total_tests > 0:
            status = "✅ ALL TESTS PASSED"
        else:
            status = "⚠️ NO TESTS FOUND"

        output += f"{'=' * 40}\n"
        output += f"{status}\n"
        output += f"{'=' * 40}\n\n"

        output += "📊 Summary:\n"
        output += f"  • Test Suites: {test_suites}\n"
        output += f"  • Total Tests: {total_tests}\n"
        output += f"  • ✅ Passed: {passed}\n"
        output += f"  • ❌ Failed: {failed}\n"
        if duration:
            output += f"  • Duration: {duration:.1f}s\n"

        failed_details = result.get("failedTestDetails", [])
        if failed_details:
            output += f"\n🔴 Failed Tests ({len(failed_details)}):\n"
            for detail in failed_details:
                output += f"  • {detail}\n"
        elif failed > 0:
            # We know there are failures but didn't capture details.
            output += f"\n🔴 {failed} test(s) failed - check TcUnit output for details\n"

        test_messages = result.get("testMessages", [])
        if test_messages and failed == 0:
            output += f"\n✅ All {total_tests} tests passed (detailed log available with {len(test_messages)} messages)\n"
    else:
        error_msg = result.get("errorMessage", "Unknown error")
        output += f"{'=' * 40}\n"
        output += "❌ TEST RUN FAILED\n"
        output += f"{'=' * 40}\n\n"
        output += f"Error: {error_msg}\n"

        test_messages = result.get("testMessages", [])
        if test_messages:
            output += "\n💬 Messages before failure:\n"
            for msg in test_messages:
                output += f"  {msg}\n"
        build_errors = result.get("buildErrors", [])
        if build_errors:
            output += "\n\n🔴 Build Errors:\n"
            for err in build_errors:
                output += f"  • {err}\n"

    return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]
