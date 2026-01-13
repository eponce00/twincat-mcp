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
- twincat_list_routes: List available ADS routes (PLCs)
- twincat_get_error_list: Get VS Error List contents (errors, warnings, messages)
- twincat_run_tcunit: Run TcUnit tests and return results
"""

import json
import subprocess
import os
import time
from pathlib import Path
from mcp.server import Server
from mcp.server.stdio import stdio_server
from mcp.types import Tool, TextContent

# =============================================================================
# SAFETY CONFIGURATION
# =============================================================================

# Armed mode TTL in seconds (default: 5 minutes)
ARMED_MODE_TTL = int(os.environ.get("TWINCAT_ARMED_TTL", 300))

# List of dangerous tools that require armed mode
DANGEROUS_TOOLS = [
    "twincat_activate",
    "twincat_restart", 
    "twincat_deploy",
    "twincat_set_state",
    "twincat_write_var"
]

# Tools that require explicit confirmation (most destructive)
CONFIRMATION_REQUIRED_TOOLS = [
    "twincat_activate",
    "twincat_restart",
    "twincat_deploy"
]

# Confirmation token format
CONFIRM_TOKEN = "CONFIRM"

# Global armed state
_armed_state = {
    "armed": False,
    "armed_at": None,
    "reason": None
}


def is_armed() -> bool:
    """Check if dangerous operations are currently armed (not expired)."""
    if not _armed_state["armed"]:
        return False
    
    if _armed_state["armed_at"] is None:
        return False
    
    elapsed = time.time() - _armed_state["armed_at"]
    if elapsed > ARMED_MODE_TTL:
        # Auto-disarm after TTL
        _armed_state["armed"] = False
        _armed_state["armed_at"] = None
        _armed_state["reason"] = None
        return False
    
    return True


def get_armed_time_remaining() -> int:
    """Get seconds remaining in armed mode, or 0 if not armed."""
    if not is_armed():
        return 0
    
    elapsed = time.time() - _armed_state["armed_at"]
    return max(0, int(ARMED_MODE_TTL - elapsed))


def arm_dangerous_operations(reason: str) -> dict:
    """Arm dangerous operations with a reason."""
    _armed_state["armed"] = True
    _armed_state["armed_at"] = time.time()
    _armed_state["reason"] = reason
    return {
        "armed": True,
        "ttl_seconds": ARMED_MODE_TTL,
        "reason": reason
    }


def disarm_dangerous_operations() -> dict:
    """Disarm dangerous operations."""
    _armed_state["armed"] = False
    _armed_state["armed_at"] = None
    _armed_state["reason"] = None
    return {"armed": False}


def check_armed_for_tool(tool_name: str, arguments: dict = None) -> tuple[bool, str]:
    """Check if a tool can be executed. Returns (allowed, message)."""
    if tool_name not in DANGEROUS_TOOLS:
        # Special case: twincat_run_tcunit requires armed mode for remote targets
        if tool_name == "twincat_run_tcunit" and arguments:
            ams_net_id = arguments.get("amsNetId", "127.0.0.1.1.1")
            # Local targets don't require arming
            if ams_net_id and not ams_net_id.startswith("127.0.0.1"):
                if not is_armed():
                    return False, (
                        f"🔒 SAFETY: Running TcUnit tests on remote PLC '{ams_net_id}' requires armed mode.\n\n"
                        f"Local testing (127.0.0.1.1.1) does not require arming.\n"
                        f"To run tests on a remote PLC:\n"
                        f"1. Call 'twincat_arm_dangerous_operations' with a reason\n"
                        f"2. Then retry this operation within {ARMED_MODE_TTL} seconds\n\n"
                        f"This safety mechanism prevents accidental PLC modifications."
                    )
        return True, ""
    
    if not is_armed():
        remaining = get_armed_time_remaining()
        return False, (
            f"🔒 SAFETY: '{tool_name}' is a dangerous operation that requires armed mode.\n\n"
            f"The server is currently in SAFE mode. To execute this operation:\n"
            f"1. Call 'twincat_arm_dangerous_operations' with a reason\n"
            f"2. Then retry this operation within {ARMED_MODE_TTL} seconds\n\n"
            f"This safety mechanism prevents accidental PLC modifications."
        )
    
    return True, f"⚠️ Armed mode active (reason: {_armed_state['reason']})"


def check_confirmation(tool_name: str, arguments: dict) -> tuple[bool, str]:
    """Check if confirmation is provided for tools that require it. Returns (confirmed, message)."""
    if tool_name not in CONFIRMATION_REQUIRED_TOOLS:
        return True, ""
    
    confirm = arguments.get("confirm", "")
    if confirm != CONFIRM_TOKEN:
        target = arguments.get("amsNetId", "unknown target")
        return False, (
            f"⚠️ CONFIRMATION REQUIRED for '{tool_name}'\n\n"
            f"This operation will affect: {target}\n\n"
            f"To proceed, add the parameter:\n"
            f"  confirm: \"{CONFIRM_TOKEN}\"\n\n"
            f"This ensures intentional execution of destructive operations."
        )
    
    return True, ""


def format_duration(seconds: float) -> str:
    """Format duration in human-readable format."""
    if seconds < 60:
        return f"{seconds:.1f}s"
    elif seconds < 3600:
        mins = int(seconds // 60)
        secs = seconds % 60
        return f"{mins}m {secs:.1f}s"
    else:
        hours = int(seconds // 3600)
        mins = int((seconds % 3600) // 60)
        return f"{hours}h {mins}m"


def add_timing_to_output(output: str, start_time: float) -> str:
    """Add execution timing to tool output."""
    elapsed = time.time() - start_time
    return f"{output}\n\n⏱️ Execution time: {format_duration(elapsed)}"


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


def run_tc_automation_with_progress(command: str, args: list[str], timeout_minutes: int = 10) -> tuple[dict, list[str]]:
    """
    Run TcAutomation.exe with progress capture from stderr.
    Returns (result_dict, progress_messages).
    """
    exe_path = find_tc_automation_exe()
    cmd = [str(exe_path), command] + args
    progress_messages = []
    
    try:
        # Use Popen for real-time stderr capture
        process = subprocess.Popen(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            cwd=str(exe_path.parent)
        )
        
        # Read stderr in a thread while process runs
        import threading
        import queue
        
        stderr_queue = queue.Queue()
        
        def read_stderr():
            for line in iter(process.stderr.readline, ''):
                if line:
                    stderr_queue.put(line.strip())
            process.stderr.close()
        
        stderr_thread = threading.Thread(target=read_stderr, daemon=True)
        stderr_thread.start()
        
        # Wait for process with timeout
        timeout_seconds = timeout_minutes * 60 + 60  # Add buffer
        try:
            stdout, _ = process.communicate(timeout=timeout_seconds)
        except subprocess.TimeoutExpired:
            process.kill()
            return {
                "success": False,
                "errorMessage": f"Command timed out after {timeout_minutes} minutes"
            }, progress_messages
        
        # Collect all progress messages
        while not stderr_queue.empty():
            try:
                line = stderr_queue.get_nowait()
                if line.startswith("[PROGRESS]"):
                    progress_messages.append(line[10:].strip())  # Remove "[PROGRESS] "
                else:
                    progress_messages.append(line)
            except queue.Empty:
                break
        
        # Parse JSON result
        if stdout.strip():
            try:
                result = json.loads(stdout)
                result["progressMessages"] = progress_messages
                return result, progress_messages
            except json.JSONDecodeError:
                return {
                    "success": False,
                    "errorMessage": f"Invalid JSON output: {stdout}",
                    "progressMessages": progress_messages
                }, progress_messages
        else:
            return {
                "success": False,
                "errorMessage": "No output from TcAutomation.exe",
                "progressMessages": progress_messages
            }, progress_messages
            
    except Exception as e:
        return {
            "success": False,
            "errorMessage": str(e),
            "progressMessages": progress_messages
        }, progress_messages


@server.list_tools()
async def list_tools() -> list[Tool]:
    """List available TwinCAT tools."""
    return [
        # Safety control tool
        Tool(
            name="twincat_arm_dangerous_operations",
            description="Arm dangerous operations for a limited time. Required before using destructive tools like deploy, activate, restart, set_state, or write_var. Armed mode expires automatically after 5 minutes (configurable via TWINCAT_ARMED_TTL env var).",
            inputSchema={
                "type": "object",
                "properties": {
                    "reason": {
                        "type": "string",
                        "description": "Reason for arming dangerous operations (e.g., 'Deploying hotfix for conveyor issue')"
                    },
                    "disarm": {
                        "type": "boolean",
                        "description": "If true, disarm instead of arm (default: false)",
                        "default": False
                    }
                },
                "required": ["reason"]
            },
            annotations={
                "readOnlyHint": False,
                "destructiveHint": False,
                "idempotentHint": True
            }
        ),
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
            },
            annotations={
                "readOnlyHint": False,
                "destructiveHint": False,
                "idempotentHint": True
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
            },
            annotations={
                "readOnlyHint": True,
                "destructiveHint": False,
                "idempotentHint": True
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
            },
            annotations={
                "readOnlyHint": False,
                "destructiveHint": False,
                "idempotentHint": True
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
            },
            annotations={
                "readOnlyHint": False,
                "destructiveHint": False,
                "idempotentHint": True
            }
        ),
        Tool(
            name="twincat_activate",
            description="Activate TwinCAT configuration on the target PLC. This downloads the configuration to the target. REQUIRES: Armed mode + confirm='CONFIRM' parameter.",
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
                    },
                    "confirm": {
                        "type": "string",
                        "description": "Safety confirmation. Must be 'CONFIRM' to execute."
                    }
                },
                "required": ["solutionPath"]
            },
            annotations={
                "readOnlyHint": False,
                "destructiveHint": True,
                "idempotentHint": False
            }
        ),
        Tool(
            name="twincat_restart",
            description="Restart TwinCAT runtime on the target PLC. REQUIRES: Armed mode + confirm='CONFIRM' parameter.",
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
                    },
                    "confirm": {
                        "type": "string",
                        "description": "Safety confirmation. Must be 'CONFIRM' to execute."
                    }
                },
                "required": ["solutionPath"]
            },
            annotations={
                "readOnlyHint": False,
                "destructiveHint": True,
                "idempotentHint": False
            }
        ),
        Tool(
            name="twincat_deploy",
            description="Full deployment workflow: build solution, activate boot project, activate configuration, and restart TwinCAT on target PLC. REQUIRES: Armed mode + confirm='CONFIRM' parameter.",
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
                    },
                    "confirm": {
                        "type": "string",
                        "description": "Safety confirmation. Must be 'CONFIRM' to execute."
                    }
                },
                "required": ["solutionPath", "amsNetId"]
            },
            annotations={
                "readOnlyHint": False,
                "destructiveHint": True,
                "idempotentHint": False
            }
        ),
        Tool(
            name="twincat_list_plcs",
            description="List all PLC projects in a TwinCAT solution with details (name, AMS port, boot project autostart status).",
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
            },
            annotations={
                "readOnlyHint": True,
                "destructiveHint": False,
                "idempotentHint": True
            }
        ),
        Tool(
            name="twincat_set_boot_project",
            description="Configure boot project settings for PLC projects (enable autostart, generate boot project on target).",
            inputSchema={
                "type": "object",
                "properties": {
                    "solutionPath": {
                        "type": "string",
                        "description": "Full path to the TwinCAT .sln file"
                    },
                    "plcName": {
                        "type": "string",
                        "description": "Target only this PLC project. Optional - targets all PLCs if not specified."
                    },
                    "autostart": {
                        "type": "boolean",
                        "description": "Enable boot project autostart (default: true)",
                        "default": True
                    },
                    "generate": {
                        "type": "boolean",
                        "description": "Generate boot project on target (default: true)",
                        "default": True
                    },
                    "tcVersion": {
                        "type": "string",
                        "description": "Force specific TwinCAT version. Optional."
                    }
                },
                "required": ["solutionPath"]
            },
            annotations={
                "readOnlyHint": False,
                "destructiveHint": False,
                "idempotentHint": True
            }
        ),
        Tool(
            name="twincat_disable_io",
            description="Disable or enable all top-level I/O devices. Useful for running tests on a different machine than the target PLC where physical hardware is not present.",
            inputSchema={
                "type": "object",
                "properties": {
                    "solutionPath": {
                        "type": "string",
                        "description": "Full path to the TwinCAT .sln file"
                    },
                    "enable": {
                        "type": "boolean",
                        "description": "If true, enable I/O devices instead of disabling (default: false = disable)",
                        "default": False
                    },
                    "tcVersion": {
                        "type": "string",
                        "description": "Force specific TwinCAT version. Optional."
                    }
                },
                "required": ["solutionPath"]
            },
            annotations={
                "readOnlyHint": False,
                "destructiveHint": False,
                "idempotentHint": True
            }
        ),
        Tool(
            name="twincat_set_variant",
            description="Get or set the TwinCAT project variant. Requires TwinCAT XAE 4024 or later.",
            inputSchema={
                "type": "object",
                "properties": {
                    "solutionPath": {
                        "type": "string",
                        "description": "Full path to the TwinCAT .sln file"
                    },
                    "variantName": {
                        "type": "string",
                        "description": "Name of the variant to set (e.g., 'PrimaryPLC'). Omit to just get current variant."
                    },
                    "tcVersion": {
                        "type": "string",
                        "description": "Force specific TwinCAT version. Optional."
                    }
                },
                "required": ["solutionPath"]
            },
            annotations={
                "readOnlyHint": False,
                "destructiveHint": False,
                "idempotentHint": True
            }
        ),
        # Phase 4: ADS Communication Tools
        Tool(
            name="twincat_get_state",
            description="Get the TwinCAT runtime state via direct ADS connection. Does NOT require Visual Studio - connects directly to the PLC. Returns: Run, Stop, Config, Error, etc.",
            inputSchema={
                "type": "object",
                "properties": {
                    "amsNetId": {
                        "type": "string",
                        "description": "AMS Net ID of the target PLC (e.g., '172.18.236.100.1.1' or '127.0.0.1.1.1' for local)"
                    },
                    "port": {
                        "type": "integer",
                        "description": "ADS port number (default: 851 for PLC runtime 1)",
                        "default": 851
                    }
                },
                "required": ["amsNetId"]
            },
            annotations={
                "readOnlyHint": True,
                "destructiveHint": False,
                "idempotentHint": True
            }
        ),
        Tool(
            name="twincat_set_state",
            description="Set the TwinCAT runtime state (Run, Stop, Config) via direct ADS connection. Note: Some targets may not support remote state changes via ADS - in that case use twincat_restart which uses the Automation Interface.",
            inputSchema={
                "type": "object",
                "properties": {
                    "amsNetId": {
                        "type": "string",
                        "description": "AMS Net ID of the target PLC (e.g., '172.18.236.100.1.1')"
                    },
                    "state": {
                        "type": "string",
                        "description": "Target state: Run, Stop, Config, or Reset"
                    },
                    "port": {
                        "type": "integer",
                        "description": "ADS port number (default: 851, auto-switches to 10000 for system state changes)",
                        "default": 851
                    }
                },
                "required": ["amsNetId", "state"]
            },
            annotations={
                "readOnlyHint": False,
                "destructiveHint": True,
                "idempotentHint": True
            }
        ),
        Tool(
            name="twincat_read_var",
            description="Read a PLC variable value via direct ADS connection. Does NOT require Visual Studio - connects directly to the PLC. Use symbol paths like 'MAIN.bMyBool' or 'GVL.nCounter'.",
            inputSchema={
                "type": "object",
                "properties": {
                    "amsNetId": {
                        "type": "string",
                        "description": "AMS Net ID of the target PLC (e.g., '172.18.236.100.1.1')"
                    },
                    "symbol": {
                        "type": "string",
                        "description": "Full symbol path of the variable (e.g., 'MAIN.bMyBool', 'GVL.nCounter')"
                    },
                    "port": {
                        "type": "integer",
                        "description": "ADS port number (default: 851 for PLC runtime 1)",
                        "default": 851
                    }
                },
                "required": ["amsNetId", "symbol"]
            },
            annotations={
                "readOnlyHint": True,
                "destructiveHint": False,
                "idempotentHint": True
            }
        ),
        Tool(
            name="twincat_write_var",
            description="Write a value to a PLC variable via direct ADS connection. Does NOT require Visual Studio - connects directly to the PLC. Supports BOOL, INT, DINT, REAL, LREAL, STRING types.",
            inputSchema={
                "type": "object",
                "properties": {
                    "amsNetId": {
                        "type": "string",
                        "description": "AMS Net ID of the target PLC (e.g., '172.18.236.100.1.1')"
                    },
                    "symbol": {
                        "type": "string",
                        "description": "Full symbol path of the variable (e.g., 'MAIN.bMyBool', 'GVL.nCounter')"
                    },
                    "value": {
                        "type": "string",
                        "description": "Value to write (will be converted to appropriate type). Examples: 'true', '42', '3.14', 'Hello'"
                    },
                    "port": {
                        "type": "integer",
                        "description": "ADS port number (default: 851 for PLC runtime 1)",
                        "default": 851
                    }
                },
                "required": ["amsNetId", "symbol", "value"]
            },
            annotations={
                "readOnlyHint": False,
                "destructiveHint": True,
                "idempotentHint": True
            }
        ),
        # Phase 4: Task Management Tools
        Tool(
            name="twincat_list_tasks",
            description="List all real-time tasks in the TwinCAT project with their configuration (priority, cycle time, enabled state).",
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
            },
            annotations={
                "readOnlyHint": True,
                "destructiveHint": False,
                "idempotentHint": True
            }
        ),
        Tool(
            name="twincat_configure_task",
            description="Configure a real-time task: enable/disable it or set autostart. Useful for enabling test tasks before running unit tests.",
            inputSchema={
                "type": "object",
                "properties": {
                    "solutionPath": {
                        "type": "string",
                        "description": "Full path to the TwinCAT .sln file"
                    },
                    "taskName": {
                        "type": "string",
                        "description": "Name of the task to configure (e.g., 'PlcTask', 'TestTask')"
                    },
                    "enable": {
                        "type": "boolean",
                        "description": "If true, enable the task. If false, disable the task. Optional."
                    },
                    "autostart": {
                        "type": "boolean",
                        "description": "If true, task starts automatically on activation. If false, requires manual start. Optional."
                    },
                    "tcVersion": {
                        "type": "string",
                        "description": "Force specific TwinCAT version. Optional."
                    }
                },
                "required": ["solutionPath", "taskName"]
            },
            annotations={
                "readOnlyHint": False,
                "destructiveHint": False,
                "idempotentHint": True
            }
        ),
        Tool(
            name="twincat_configure_rt",
            description="Configure TwinCAT real-time settings: max CPU cores for isolated cores and CPU load limit percentage.",
            inputSchema={
                "type": "object",
                "properties": {
                    "solutionPath": {
                        "type": "string",
                        "description": "Full path to the TwinCAT .sln file"
                    },
                    "maxCpus": {
                        "type": "integer",
                        "description": "Maximum number of CPU cores for isolated real-time cores (1-based). Default: 1"
                    },
                    "loadLimit": {
                        "type": "integer",
                        "description": "CPU load limit percentage (1-100). Default: 50"
                    },
                    "tcVersion": {
                        "type": "string",
                        "description": "Force specific TwinCAT version. Optional."
                    }
                },
                "required": ["solutionPath"]
            },
            annotations={
                "readOnlyHint": False,
                "destructiveHint": False,
                "idempotentHint": True
            }
        ),
        # Code Analysis Tools
        Tool(
            name="twincat_check_all_objects",
            description="Check all PLC objects including unused ones. This catches compile errors in function blocks that aren't referenced anywhere - errors that a normal build would miss.",
            inputSchema={
                "type": "object",
                "properties": {
                    "solutionPath": {
                        "type": "string",
                        "description": "Full path to the TwinCAT .sln file"
                    },
                    "plcName": {
                        "type": "string",
                        "description": "Target only this PLC project. Optional - checks all PLCs if not specified."
                    },
                    "tcVersion": {
                        "type": "string",
                        "description": "Force specific TwinCAT version. Optional."
                    }
                },
                "required": ["solutionPath"]
            },
            annotations={
                "readOnlyHint": True,
                "destructiveHint": False,
                "idempotentHint": True
            }
        ),
        Tool(
            name="twincat_static_analysis",
            description="Run static code analysis on PLC projects. Checks coding rules, naming conventions, and best practices. Requires TE1200 license.",
            inputSchema={
                "type": "object",
                "properties": {
                    "solutionPath": {
                        "type": "string",
                        "description": "Full path to the TwinCAT .sln file"
                    },
                    "checkAll": {
                        "type": "boolean",
                        "description": "Check all objects including unused ones (default: true)",
                        "default": True
                    },
                    "plcName": {
                        "type": "string",
                        "description": "Target only this PLC project. Optional - analyzes all PLCs if not specified."
                    },
                    "tcVersion": {
                        "type": "string",
                        "description": "Force specific TwinCAT version. Optional."
                    }
                },
                "required": ["solutionPath"]
            },
            annotations={
                "readOnlyHint": True,
                "destructiveHint": False,
                "idempotentHint": True
            }
        ),
        Tool(
            name="twincat_list_routes",
            description="List all configured ADS routes (PLCs) from TwinCAT. Shows available targets with their names, IP addresses, and AMS Net IDs. Useful for discovering PLCs before connecting.",
            inputSchema={
                "type": "object",
                "properties": {},
                "required": []
            },
            annotations={
                "readOnlyHint": True,
                "destructiveHint": False,
                "idempotentHint": True
            }
        ),
        Tool(
            name="twincat_get_error_list",
            description="Get contents of Visual Studio Error List window. Returns errors, warnings, and messages (including ADS logs from running PLC). Useful for viewing runtime messages, diagnostics, or any output that appears in the VS Error List.",
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
                    },
                    "includeMessages": {
                        "type": "boolean",
                        "description": "Include messages (ADS logs, etc.). Default: true",
                        "default": True
                    },
                    "includeWarnings": {
                        "type": "boolean",
                        "description": "Include warnings. Default: true",
                        "default": True
                    },
                    "includeErrors": {
                        "type": "boolean",
                        "description": "Include errors. Default: true",
                        "default": True
                    },
                    "waitSeconds": {
                        "type": "integer",
                        "description": "Wait N seconds before reading (for async messages). Default: 0",
                        "default": 0
                    }
                },
                "required": ["solutionPath"]
            },
            annotations={
                "readOnlyHint": True,
                "destructiveHint": False,
                "idempotentHint": True
            }
        ),
        Tool(
            name="twincat_run_tcunit",
            description="Run TcUnit tests on a TwinCAT PLC project and return results. Handles full test workflow: build, configure task, set boot project, optionally disable I/O, activate, restart, and poll for results. Returns test counts (passed/failed) and individual test results.",
            inputSchema={
                "type": "object",
                "properties": {
                    "solutionPath": {
                        "type": "string",
                        "description": "Full path to the TwinCAT .sln file"
                    },
                    "amsNetId": {
                        "type": "string",
                        "description": "Target AMS Net ID (default: 127.0.0.1.1.1 for local)"
                    },
                    "taskName": {
                        "type": "string",
                        "description": "Name of the task running TcUnit tests (auto-detected if only one task)"
                    },
                    "plcName": {
                        "type": "string",
                        "description": "Target only this PLC project"
                    },
                    "tcVersion": {
                        "type": "string",
                        "description": "Force specific TwinCAT version. Optional."
                    },
                    "timeoutMinutes": {
                        "type": "integer",
                        "description": "Timeout in minutes (default: 10)",
                        "default": 10
                    },
                    "disableIo": {
                        "type": "boolean",
                        "description": "Disable I/O devices for running without hardware (default: false)",
                        "default": False
                    },
                    "skipBuild": {
                        "type": "boolean",
                        "description": "Skip building the solution (default: false)",
                        "default": False
                    }
                },
                "required": ["solutionPath"]
            },
            annotations={
                "readOnlyHint": False,
                "destructiveHint": True,
                "idempotentHint": False
            }
        )
    ]


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
        
        return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]
    
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
        
        return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]
    
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
        
        return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]
    
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
        
        return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]
    
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
        
        return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]
    
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
        
        return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]
    
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
        
        return [TextContent(type="text", text=add_timing_to_output(output, tool_start_time))]
    
    elif name == "twincat_list_plcs":
        solution_path = arguments.get("solutionPath", "")
        tc_version = arguments.get("tcVersion")
        
        args = ["--solution", solution_path]
        if tc_version:
            args.extend(["--tcversion", tc_version])
        
        result = run_tc_automation("list-plcs", args)
        
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
        
        args = ["--solution", solution_path]
        if plc_name:
            args.extend(["--plc", plc_name])
        if autostart:
            args.append("--autostart")
        if generate:
            args.append("--generate")
        if tc_version:
            args.extend(["--tcversion", tc_version])
        
        result = run_tc_automation("set-boot-project", args)
        
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
        
        args = ["--solution", solution_path]
        if enable:
            args.append("--enable")
        if tc_version:
            args.extend(["--tcversion", tc_version])
        
        result = run_tc_automation("disable-io", args)
        
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
        
        args = ["--solution", solution_path]
        if variant_name:
            args.extend(["--variant", variant_name])
        else:
            args.append("--get")
        if tc_version:
            args.extend(["--tcversion", tc_version])
        
        result = run_tc_automation("set-variant", args)
        
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
        
        args = ["--amsnetid", ams_net_id, "--port", str(port)]
        
        result = run_tc_automation("get-state", args)
        
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
        
        args = ["--amsnetid", ams_net_id, "--state", state, "--port", str(port)]
        
        result = run_tc_automation("set-state", args)
        
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
        
        args = ["--amsnetid", ams_net_id, "--symbol", symbol, "--port", str(port)]
        
        result = run_tc_automation("read-var", args)
        
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
        
        args = ["--amsnetid", ams_net_id, "--symbol", symbol, "--value", value, "--port", str(port)]
        
        result = run_tc_automation("write-var", args)
        
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
        
        args = ["--solution", solution_path]
        if tc_version:
            args.extend(["--tcversion", tc_version])
        
        result = run_tc_automation("list-tasks", args)
        
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
        
        args = ["--solution", solution_path, "--task", task_name]
        if enable is True:
            args.append("--enable")
        elif enable is False:
            args.append("--disable")
        if autostart is True:
            args.append("--autostart")
        elif autostart is False:
            args.append("--no-autostart")
        if tc_version:
            args.extend(["--tcversion", tc_version])
        
        result = run_tc_automation("configure-task", args)
        
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
        
        args = ["--solution", solution_path]
        if max_cpus is not None:
            args.extend(["--max-cpus", str(max_cpus)])
        if load_limit is not None:
            args.extend(["--load-limit", str(load_limit)])
        if tc_version:
            args.extend(["--tcversion", tc_version])
        
        result = run_tc_automation("configure-rt", args)
        
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
        
        args = ["--solution", solution_path]
        if plc_name:
            args.extend(["--plc", plc_name])
        if tc_version:
            args.extend(["--tcversion", tc_version])
        
        result = run_tc_automation("check-all-objects", args)
        
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
        
        args = ["--solution", solution_path]
        if check_all:
            args.append("--check-all")
        if plc_name:
            args.extend(["--plc", plc_name])
        if tc_version:
            args.extend(["--tcversion", tc_version])
        
        result = run_tc_automation("static-analysis", args)
        
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
        
        args = ["--solution", solution_path]
        if tc_version:
            args.extend(["--tcversion", tc_version])
        if not include_messages:
            args.append("--messages=false")
        else:
            args.append("--messages")
        if not include_warnings:
            args.append("--warnings=false")
        else:
            args.append("--warnings")
        if not include_errors:
            args.append("--errors=false")
        else:
            args.append("--errors")
        if wait_seconds > 0:
            args.extend(["--wait", str(wait_seconds)])
        
        result = run_tc_automation("get-error-list", args)
        
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
        
        args = ["--solution", solution_path]
        if ams_net_id:
            args.extend(["--amsnetid", ams_net_id])
        if task_name:
            args.extend(["--task", task_name])
        if plc_name:
            args.extend(["--plc", plc_name])
        if tc_version:
            args.extend(["--tcversion", tc_version])
        if timeout_minutes != 10:
            args.extend(["--timeout", str(timeout_minutes)])
        if disable_io:
            args.append("--disable-io")
        if skip_build:
            args.append("--skip-build")
        
        # Use streaming function to capture progress
        result, progress_messages = run_tc_automation_with_progress("run-tcunit", args, timeout_minutes)
        
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
            
            # Show failed test details if any
            failed_details = result.get("failedTestDetails", [])
            if failed_details:
                output += f"\n🔴 Failed Tests:\n"
                for detail in failed_details:
                    output += f"  • {detail}\n"
            
            # Show test messages (TcUnit output)
            test_messages = result.get("testMessages", [])
            if test_messages:
                output += f"\n💬 TcUnit Output:\n"
                for msg in test_messages[-20:]:  # Last 20 messages
                    output += f"  {msg}\n"
                if len(test_messages) > 20:
                    output += f"  ... and {len(test_messages) - 20} more messages\n"
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
