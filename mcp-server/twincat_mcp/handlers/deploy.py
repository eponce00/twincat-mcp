"""
Deploy handler. Like the shell-routed tools it goes through
`run_shell_step`, but it owns a chunky output formatter (deployment
steps, PLC list, dry-run prefix) so it lives in its own module to keep
`shell.py` readable.
"""

from mcp.types import TextContent

from ..dispatch import run_shell_step
from ..formatting import add_timing_to_output
from ._registry import register


@register("twincat_deploy")
async def handle_deploy(arguments: dict, tool_start_time: float) -> list[TextContent]:
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
