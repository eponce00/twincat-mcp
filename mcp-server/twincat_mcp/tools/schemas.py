"""
MCP Tool() descriptors for all TwinCAT tools exposed by the server.

Separating the schemas from the dispatch logic makes both easier to
maintain: you can eyeball or diff the schema list without scrolling past
the handler bodies, and vice versa.

This is pure data — no runtime side effects. The `server.py` entry point
calls `get_tool_schemas()` inside its `@server.list_tools()` handler.
"""

from mcp.types import Tool


def get_tool_schemas() -> list[Tool]:
    """Return the list of Tool descriptors advertised via list_tools."""
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
            name="twincat_batch",
            description=(
                "Run an ordered sequence of TwinCAT operations against a SINGLE shared "
                "Visual Studio / TcXaeShell instance. The shell is opened once up-front "
                "(only if any step requires it) and closed after the last step, so you "
                "only pay the ~40s-1m30s VS startup cost once instead of per call.\n\n"
                "Use this whenever you want to chain 2+ shell-based tools "
                "(e.g. set-target + set-boot-project + build + activate + restart). "
                "Each step is a {id, command, args} object. Steps run sequentially and, "
                "by default, the batch stops at the first failing step. Step results are "
                "returned in order. ADS-only steps (get-state/set-state/read-var/write-var) "
                "run directly without touching the shell.\n\n"
                "Supported step commands:\n"
                "  SHELL-based: build, info, clean, set-target, activate, restart, "
                "list-plcs, set-boot-project, disable-io, set-variant, list-tasks, "
                "configure-task, configure-rt, check-all-objects, static-analysis, "
                "generate-library, get-error-list\n"
                "  ADS-only:    get-state, set-state, read-var, write-var\n\n"
                "NOT supported in batch: deploy, run-tcunit (use their dedicated tools).\n\n"
                "Safety: If any step is a dangerous command (activate, restart, set-state, "
                "write-var), armed mode is required. If any step is activate or restart, "
                "confirm='CONFIRM' is also required at the batch level."
            ),
            inputSchema={
                "type": "object",
                "properties": {
                    "solutionPath": {
                        "type": "string",
                        "description": "Full path to the TwinCAT .sln file. Required if any step uses a shell-based command."
                    },
                    "tcVersion": {
                        "type": "string",
                        "description": "Force specific TwinCAT version (e.g., '3.1.4026.17'). Optional."
                    },
                    "stopOnError": {
                        "type": "boolean",
                        "description": "Stop the batch at the first failing step (default: true).",
                        "default": True
                    },
                    "timeoutMinutes": {
                        "type": "integer",
                        "description": "Overall batch timeout in minutes (default: 15). Includes VS startup + all steps.",
                        "default": 15
                    },
                    "steps": {
                        "type": "array",
                        "description": "Ordered list of steps to execute.",
                        "items": {
                            "type": "object",
                            "properties": {
                                "id": {
                                    "type": "string",
                                    "description": "Optional human-friendly id for this step (appears in logs and results)."
                                },
                                "command": {
                                    "type": "string",
                                    "description": (
                                        "The low-level command to run. One of: "
                                        "build, info, clean, set-target, activate, restart, "
                                        "list-plcs, set-boot-project, disable-io, set-variant, "
                                        "list-tasks, configure-task, configure-rt, "
                                        "check-all-objects, static-analysis, generate-library, "
                                        "get-error-list, get-state, set-state, read-var, write-var"
                                    )
                                },
                                "args": {
                                    "type": "object",
                                    "description": (
                                        "Per-command arguments. Mirrors the arguments of the "
                                        "corresponding twincat_* tool (amsNetId, plcName, taskName, "
                                        "symbol, value, enable, autostart, checkAll, waitSeconds, "
                                        "maxCpus, loadLimit, variantName, libraryLocation, skipBuild, "
                                        "dryRun, includeErrors, includeWarnings, includeMessages, "
                                        "port, state, clean, etc.). solutionPath and tcVersion are "
                                        "inherited from the batch top level."
                                    )
                                }
                            },
                            "required": ["command"]
                        },
                        "minItems": 1
                    },
                    "confirm": {
                        "type": "string",
                        "description": "Safety confirmation. Must be 'CONFIRM' if any step is 'activate' or 'restart'."
                    }
                },
                "required": ["steps"]
            },
            annotations={
                "readOnlyHint": False,
                "destructiveHint": True,
                "idempotentHint": False
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
            name="twincat_generate_library",
            description="Generate a TwinCAT .library artifact from a specific PLC project in a solution. Defaults output to the solution directory when no location is provided.",
            inputSchema={
                "type": "object",
                "properties": {
                    "solutionPath": {
                        "type": "string",
                        "description": "Full path to the TwinCAT .sln file"
                    },
                    "plcName": {
                        "type": "string",
                        "description": "PLC project name to export as a .library"
                    },
                    "libraryLocation": {
                        "type": "string",
                        "description": "Optional output directory or explicit .library file path"
                    },
                    "tcVersion": {
                        "type": "string",
                        "description": "Force specific TwinCAT version. Optional."
                    },
                    "skipBuild": {
                        "type": "boolean",
                        "description": "Skip build before export (default: false)",
                        "default": False
                    },
                    "dryRun": {
                        "type": "boolean",
                        "description": "Validate flow without exporting (default: false)",
                        "default": False
                    }
                },
                "required": ["solutionPath", "plcName"]
            },
            annotations={
                "readOnlyHint": False,
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
        ),
        Tool(
            name="twincat_kill_stale",
            description=(
                "SURGICAL cleanup of stale/orphaned TwinCAT shells. "
                "Tears down this MCP server's own persistent shell host + DTE, "
                "then reaps orphaned hosts/DTEs from crashed MCP sessions using "
                "recorded session-file PIDs (verified by process start-time). "
                "NEVER kills TcXaeShell/devenv by image name or window-title heuristic — "
                "your open IDE is safe. Only PIDs explicitly recorded in our own "
                "session files are ever touched. "
                "Use when a build fails with RPC (0x800706BE) or COM errors."
            ),
            inputSchema={
                "type": "object",
                "properties": {},
                "required": []
            },
            annotations={
                "readOnlyHint": False,
                "destructiveHint": True,
                "idempotentHint": True
            }
        ),
        Tool(
            name="twincat_host_status",
            description=(
                "Report status of the persistent TwinCAT shell host: whether it's running, "
                "its PID, the DTE PID it owns, the currently-loaded solution, and uptime. "
                "Read-only; never starts the host (it is spawned lazily on the first shell-needing tool call)."
            ),
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
        )
    ]
