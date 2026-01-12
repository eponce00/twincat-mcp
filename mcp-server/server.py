"""
TwinCAT MCP Server

This MCP server exposes TwinCAT automation tools to AI assistants like GitHub Copilot.
It wraps the TcAutomation.exe CLI tool which provides access to the TwinCAT Automation Interface.

Tools:
- twincat_build: Build a TwinCAT solution and return errors/warnings
- twincat_get_info: Get information about a TwinCAT solution
- twincat_clean: Clean a TwinCAT solution
- twincat_set_target: Set target AMS Net ID
- twincat_activate: Activate configuration on target PLC
- twincat_restart: Restart TwinCAT runtime on target
- twincat_deploy: Full deployment workflow
"""

import json
import subprocess
import os
from pathlib import Path
from mcp.server import Server
from mcp.server.stdio import stdio_server
from mcp.types import Tool, TextContent

# Initialize MCP server
server = Server("twincat-mcp")

# Path to TcAutomation.exe (relative to this script)
SCRIPT_DIR = Path(__file__).parent
TC_AUTOMATION_EXE = SCRIPT_DIR.parent / "TcAutomation" / "bin" / "Release" / "TcAutomation.exe"

# Alternative paths to check (in order of preference)
TC_AUTOMATION_PATHS = [
    # .NET Framework 4.7.2 build output (current)
    SCRIPT_DIR.parent / "TcAutomation" / "bin" / "Release" / "TcAutomation.exe",
    SCRIPT_DIR.parent / "TcAutomation" / "bin" / "Debug" / "TcAutomation.exe",
    # Legacy .NET 8 paths (in case someone builds with that)
    SCRIPT_DIR.parent / "TcAutomation" / "bin" / "Release" / "net8.0-windows" / "TcAutomation.exe",
    SCRIPT_DIR.parent / "TcAutomation" / "publish" / "TcAutomation.exe",
]


def find_tc_automation_exe() -> Path:
    """Find the TcAutomation.exe executable."""
    for path in TC_AUTOMATION_PATHS:
        if path.exists():
            return path
    raise FileNotFoundError(
        f"TcAutomation.exe not found. Searched paths:\n" + 
        "\n".join(f"  - {p}" for p in TC_AUTOMATION_PATHS) +
        "\n\nPlease build the TcAutomation project first:\n" +
        "  .\\scripts\\build.ps1"
    )


def run_tc_automation(command: str, args: list[str]) -> dict:
    """Run TcAutomation.exe with the given command and arguments."""
    exe_path = find_tc_automation_exe()
    
    cmd = [str(exe_path), command] + args
    
    try:
        result = subprocess.run(
            cmd,
            capture_output=True,
            text=True,
            timeout=300,  # 5 minute timeout
            cwd=str(exe_path.parent)
        )
        
        # Try to parse JSON output
        if result.stdout.strip():
            try:
                return json.loads(result.stdout)
            except json.JSONDecodeError:
                return {
                    "success": False,
                    "errorMessage": f"Invalid JSON output: {result.stdout}",
                    "stderr": result.stderr
                }
        else:
            return {
                "success": False,
                "errorMessage": result.stderr or "No output from TcAutomation.exe"
            }
            
    except subprocess.TimeoutExpired:
        return {
            "success": False,
            "errorMessage": "Command timed out after 5 minutes"
        }
    except Exception as e:
        return {
            "success": False,
            "errorMessage": str(e)
        }


@server.list_tools()
async def list_tools() -> list[Tool]:
    """List available TwinCAT tools."""
    return [
        Tool(
            name="twincat_build",
            description="Build a TwinCAT solution and return any compile errors or warnings. Use this to validate TwinCAT/PLC code changes.",
            inputSchema={
                "type": "object",
                "properties": {
                    "solutionPath": {
                        "type": "string",
                        "description": "Full path to the TwinCAT .sln file"
                    },
                    "clean": {
                        "type": "boolean",
                        "description": "Clean solution before building (default: true)",
                        "default": True
                    },
                    "tcVersion": {
                        "type": "string",
                        "description": "Force specific TwinCAT version (e.g., '3.1.4026.17'). Optional."
                    }
                },
                "required": ["solutionPath"]
            }
        ),
        Tool(
            name="twincat_get_info",
            description="Get information about a TwinCAT solution including version, PLC projects, and configuration.",
            inputSchema={
                "type": "object",
                "properties": {
                    "solutionPath": {
                        "type": "string",
                        "description": "Full path to the TwinCAT .sln file"
                    }
                },
                "required": ["solutionPath"]
            }
        ),
        Tool(
            name="twincat_clean",
            description="Clean a TwinCAT solution (remove build artifacts).",
            inputSchema={
                "type": "object",
                "properties": {
                    "solutionPath": {
                        "type": "string",
                        "description": "Full path to the TwinCAT .sln file"
                    },
                    "tcVersion": {
                        "type": "string",
                        "description": "Force specific TwinCAT version. Optional."
                    }
                },
                "required": ["solutionPath"]
            }
        ),
        Tool(
            name="twincat_set_target",
            description="Set the target AMS Net ID for deployment without activating.",
            inputSchema={
                "type": "object",
                "properties": {
                    "solutionPath": {
                        "type": "string",
                        "description": "Full path to the TwinCAT .sln file"
                    },
                    "amsNetId": {
                        "type": "string",
                        "description": "Target AMS Net ID (e.g., '5.22.157.86.1.1')"
                    },
                    "tcVersion": {
                        "type": "string",
                        "description": "Force specific TwinCAT version. Optional."
                    }
                },
                "required": ["solutionPath", "amsNetId"]
            }
        ),
        Tool(
            name="twincat_activate",
            description="Activate TwinCAT configuration on the target PLC. This downloads the configuration to the target.",
            inputSchema={
                "type": "object",
                "properties": {
                    "solutionPath": {
                        "type": "string",
                        "description": "Full path to the TwinCAT .sln file"
                    },
                    "amsNetId": {
                        "type": "string",
                        "description": "Target AMS Net ID. Optional - uses project default if not specified."
                    },
                    "tcVersion": {
                        "type": "string",
                        "description": "Force specific TwinCAT version. Optional."
                    }
                },
                "required": ["solutionPath"]
            }
        ),
        Tool(
            name="twincat_restart",
            description="Restart TwinCAT runtime on the target PLC.",
            inputSchema={
                "type": "object",
                "properties": {
                    "solutionPath": {
                        "type": "string",
                        "description": "Full path to the TwinCAT .sln file"
                    },
                    "amsNetId": {
                        "type": "string",
                        "description": "Target AMS Net ID. Optional - uses project default if not specified."
                    },
                    "tcVersion": {
                        "type": "string",
                        "description": "Force specific TwinCAT version. Optional."
                    }
                },
                "required": ["solutionPath"]
            }
        ),
        Tool(
            name="twincat_deploy",
            description="Full deployment workflow: build solution, activate boot project, activate configuration, and restart TwinCAT on target PLC.",
            inputSchema={
                "type": "object",
                "properties": {
                    "solutionPath": {
                        "type": "string",
                        "description": "Full path to the TwinCAT .sln file"
                    },
                    "amsNetId": {
                        "type": "string",
                        "description": "Target AMS Net ID (e.g., '5.22.157.86.1.1')"
                    },
                    "plcName": {
                        "type": "string",
                        "description": "Deploy only this PLC project. Optional - deploys all PLCs if not specified."
                    },
                    "tcVersion": {
                        "type": "string",
                        "description": "Force specific TwinCAT version. Optional."
                    },
                    "skipBuild": {
                        "type": "boolean",
                        "description": "Skip building the solution (default: false)",
                        "default": False
                    },
                    "dryRun": {
                        "type": "boolean",
                        "description": "Show what would be done without making changes (default: false)",
                        "default": False
                    }
                },
                "required": ["solutionPath", "amsNetId"]
            }
        )
    ]


@server.call_tool()
async def call_tool(name: str, arguments: dict) -> list[TextContent]:
    """Handle tool calls."""
    
    if name == "twincat_build":
        solution_path = arguments.get("solutionPath", "")
        clean = arguments.get("clean", True)
        tc_version = arguments.get("tcVersion")
        
        args = ["--solution", solution_path]
        if clean:
            args.append("--clean")
        if tc_version:
            args.extend(["--tcversion", tc_version])
        
        result = run_tc_automation("build", args)
        
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
                output += f"\nError: {result['errorMessage']}\n"
            if result.get("errors"):
                output += "\n🔴 Errors:\n"
                for e in result["errors"]:
                    output += f"  - {e.get('fileName', '')}:{e.get('line', '')}: {e.get('description', '')}\n"
            if result.get("warnings"):
                output += "\n⚠️ Warnings:\n"
                for w in result["warnings"]:
                    output += f"  - {w.get('fileName', '')}:{w.get('line', '')}: {w.get('description', '')}\n"
        
        return [TextContent(type="text", text=output)]
    
    elif name == "twincat_get_info":
        solution_path = arguments.get("solutionPath", "")
        
        result = run_tc_automation("info", ["--solution", solution_path])
        
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
        
        return [TextContent(type="text", text=output)]
    
    elif name == "twincat_clean":
        solution_path = arguments.get("solutionPath", "")
        tc_version = arguments.get("tcVersion")
        
        args = ["--solution", solution_path]
        if tc_version:
            args.extend(["--tcversion", tc_version])
        
        result = run_tc_automation("clean", args)
        
        if result.get("success"):
            output = f"✅ {result.get('message', 'Solution cleaned successfully')}"
        else:
            output = f"❌ Clean failed: {result.get('error', 'Unknown error')}"
        
        return [TextContent(type="text", text=output)]
    
    elif name == "twincat_set_target":
        solution_path = arguments.get("solutionPath", "")
        ams_net_id = arguments.get("amsNetId", "")
        tc_version = arguments.get("tcVersion")
        
        args = ["--solution", solution_path, "--amsnetid", ams_net_id]
        if tc_version:
            args.extend(["--tcversion", tc_version])
        
        result = run_tc_automation("set-target", args)
        
        if result.get("success"):
            output = f"✅ {result.get('message', 'Target set successfully')}\n"
            output += f"Previous target: {result.get('previousTarget', 'Unknown')}\n"
            output += f"New target: {result.get('newTarget', ams_net_id)}"
        else:
            output = f"❌ Set target failed: {result.get('error', 'Unknown error')}"
        
        return [TextContent(type="text", text=output)]
    
    elif name == "twincat_activate":
        solution_path = arguments.get("solutionPath", "")
        ams_net_id = arguments.get("amsNetId")
        tc_version = arguments.get("tcVersion")
        
        args = ["--solution", solution_path]
        if ams_net_id:
            args.extend(["--amsnetid", ams_net_id])
        if tc_version:
            args.extend(["--tcversion", tc_version])
        
        result = run_tc_automation("activate", args)
        
        if result.get("success"):
            output = f"✅ {result.get('message', 'Configuration activated')}\n"
            output += f"Target: {result.get('targetNetId', 'Unknown')}"
        else:
            output = f"❌ Activation failed: {result.get('error', 'Unknown error')}"
        
        return [TextContent(type="text", text=output)]
    
    elif name == "twincat_restart":
        solution_path = arguments.get("solutionPath", "")
        ams_net_id = arguments.get("amsNetId")
        tc_version = arguments.get("tcVersion")
        
        args = ["--solution", solution_path]
        if ams_net_id:
            args.extend(["--amsnetid", ams_net_id])
        if tc_version:
            args.extend(["--tcversion", tc_version])
        
        result = run_tc_automation("restart", args)
        
        if result.get("success"):
            output = f"✅ {result.get('message', 'TwinCAT restarted')}\n"
            output += f"Target: {result.get('targetNetId', 'Unknown')}"
        else:
            output = f"❌ Restart failed: {result.get('error', 'Unknown error')}"
        
        return [TextContent(type="text", text=output)]
    
    elif name == "twincat_deploy":
        solution_path = arguments.get("solutionPath", "")
        ams_net_id = arguments.get("amsNetId", "")
        plc_name = arguments.get("plcName")
        tc_version = arguments.get("tcVersion")
        skip_build = arguments.get("skipBuild", False)
        dry_run = arguments.get("dryRun", False)
        
        args = ["--solution", solution_path, "--amsnetid", ams_net_id]
        if plc_name:
            args.extend(["--plc", plc_name])
        if tc_version:
            args.extend(["--tcversion", tc_version])
        if skip_build:
            args.append("--skip-build")
        if dry_run:
            args.append("--dry-run")
        
        result = run_tc_automation("deploy", args)
        
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
        
        return [TextContent(type="text", text=output)]
    
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
