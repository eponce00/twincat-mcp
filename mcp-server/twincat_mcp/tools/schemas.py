"""
MCP Tool() descriptors for all TwinCAT tools exposed by the server.

Separating the schemas from the dispatch logic makes both easier to
maintain: you can eyeball or diff the schema list without scrolling past
the handler bodies, and vice versa.

This is pure data — no runtime side effects. The `server.py` entry point
calls `get_tool_schemas()` inside its `@server.list_tools()` handler.
"""

from mcp.types import Tool

from ..defaults import DEFAULT_AMS_NET_ID, describe_default_for_schema


# Description suffix for every `amsNetId` schema field. Resolved at
# import-time from the env var so agents see the effective default in
# `list_tools` output without making a round-trip call.
_AMS_NET_ID_DESC = describe_default_for_schema()


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
            description=(
                "Build a TwinCAT solution and return any compile errors or "
                "warnings. Use this to validate TwinCAT/PLC code changes.\n\n"
                "Response shape:\n"
                "  success=True  →  Top line 'Build succeeded with N "
                "warning(s)'. Warnings listed as "
                "'<fileName>:<line>: <description>' (one per line).\n"
                "  success=False →  Top line 'Build failed with N error(s) "
                "and M warning(s)'. Errors and warnings in separate "
                "'🔴 Errors:' / '⚠️ Warnings:' blocks, each line formatted "
                "'<fileName>:<line>: <description>'. Catastrophic failures "
                "(solution missing, stale TcXaeShell lock, RPC/COM error) "
                "add an 'Error: <message>' section, and RPC failures "
                "nudge the agent toward `twincat_kill_stale`.\n\n"
                "Safety-critical warnings to watch for:\n"
                "  • C0297 — 'Possible Stack Overflow' — the compiler "
                "estimates the task stack usage exceeds the configured "
                "stack size. This is a precursor to a runtime OS crash on "
                "activation; if you see it, DO NOT activate without "
                "increasing the task stack first (or you'll take the "
                "target's Windows down and lose the ADS connection).\n"
            ),
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
                        "description": f"Target AMS Net ID (e.g., '5.22.157.86.1.1'). {_AMS_NET_ID_DESC}"
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
                        "description": f"Target AMS Net ID. {_AMS_NET_ID_DESC}"
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
                        "description": f"Target AMS Net ID. {_AMS_NET_ID_DESC}"
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
                        "description": f"Target AMS Net ID (e.g., '5.22.157.86.1.1'). {_AMS_NET_ID_DESC}"
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
                "required": ["solutionPath"]
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
            name="twincat_list_symbols",
            description=(
                "Enumerate PLC symbols on a target over ADS. No solution "
                "required — reads the symbol table straight from the "
                "running runtime. Use this when `twincat_read_var` returns "
                "`DeviceSymbolNotFound (0x710)` and you need to see what's "
                "actually loaded, or to discover paths under a known "
                "prefix (e.g. a TcUnit test FB's Status.State chain).\n\n"
                "Filtering happens server-side so you don't pay JSON "
                "serialization on hundreds of irrelevant symbols:\n"
                "  • prefix='MAIN.'                → top-level globals\n"
                "  • contains='fbStateMachine'      → find by FB name\n"
                "  • prefix='MAIN.', contains='Status' → both\n\n"
                "Requires the runtime in Run or Stop. If the target is "
                "rebooting (typical right after activate), the handler "
                "surfaces that explicitly and nudges you to "
                "`twincat_ping_target`."
            ),
            inputSchema={
                "type": "object",
                "properties": {
                    "amsNetId": {
                        "type": "string",
                        "description": f"Target AMS Net ID. {_AMS_NET_ID_DESC}"
                    },
                    "port": {
                        "type": "integer",
                        "description": "AMS port (default: 851 for PLC runtime 1)",
                        "default": 851
                    },
                    "prefix": {
                        "type": "string",
                        "description": "Case-insensitive prefix on the full symbol path (e.g., 'MAIN.')"
                    },
                    "contains": {
                        "type": "string",
                        "description": "Case-insensitive substring anywhere in the symbol path"
                    },
                    "max": {
                        "type": "integer",
                        "description": "Cap on returned entries (default: 200). TotalMatched is reported separately so you can widen if needed.",
                        "default": 200
                    },
                    "includeTypes": {
                        "type": "boolean",
                        "description": "Include type name + size per symbol (default: false; bump when you need the schema to write or cast)",
                        "default": False
                    }
                },
                "required": []
            },
            annotations={
                "readOnlyHint": True,
                "destructiveHint": False,
                "idempotentHint": True
            }
        ),
        Tool(
            name="twincat_read_plc_log",
            description=(
                "Tail the TwinCAT event log on a target over ADS (port 110, "
                "TcEventLogger). Listens for `waitSeconds` and returns "
                "every message/alarm emitted during that window — use this "
                "when you need runtime logs and can't go through "
                "`twincat_get_error_list` (no solution loaded, wrong "
                "solution loaded, or target rebooted after a crash).\n\n"
                "Captures:\n"
                "  • AdsLogStr() output from PLC code\n"
                "  • _Raise / TcEventLogger.Raise events\n"
                "  • TwinCAT system messages (PLC state changes, licence "
                "diagnostics, ADS router events, etc.)\n\n"
                "Tips:\n"
                "  • This is a LISTEN window, not a history dump — past "
                "events aren't retrieved. If you want history, call this "
                "BEFORE triggering the operation you expect to log.\n"
                "  • Pair with a `contains` filter to ignore library-reload "
                "noise (e.g., `contains='FAILED TEST'`).\n"
                "  • For remote targets the local machine needs the "
                "TcEventLogger COM proxy (bundled with TC3 XAE/XAR)."
            ),
            inputSchema={
                "type": "object",
                "properties": {
                    "amsNetId": {
                        "type": "string",
                        "description": f"Target AMS Net ID. {_AMS_NET_ID_DESC}"
                    },
                    "waitSeconds": {
                        "type": "integer",
                        "description": "Seconds to listen for new log events (default: 5). Reaches back only over this window, not historical.",
                        "default": 5
                    },
                    "contains": {
                        "type": "string",
                        "description": "Case-insensitive substring filter on the message body"
                    },
                    "max": {
                        "type": "integer",
                        "description": "Cap on returned events (default: 200)",
                        "default": 200
                    }
                },
                "required": []
            },
            annotations={
                "readOnlyHint": True,
                "destructiveHint": False,
                "idempotentHint": True
            }
        ),
        Tool(
            name="twincat_ping_target",
            description=(
                "Classify the reachability of a TwinCAT target over ADS. "
                "Unlike every other tool, this has an explicit per-probe "
                "timeout (default 2.5s each, two probes = ~5s worst case), "
                "so it NEVER hangs — use it as the first call after any "
                "'connection closed' or timeout error from another tool "
                "to tell what actually went wrong.\n\n"
                "Classifications:\n"
                "  • reachable     → OS and runtime are up, runtime is in "
                "Run. Safe to retry the failed call.\n"
                "  • rebooting     → OS is up (system service answers) but "
                "the PLC runtime is stopped, starting, or recovering. "
                "Typical after `twincat_activate` or `twincat_restart`, or "
                "after a stack-overflow crash brought the runtime down "
                "without crashing Windows. Retry in a few seconds.\n"
                "  • unreachable   → AMS system service (port 10000) does "
                "not answer. Target is powered off, network cable pulled, "
                "firewall is blocking, or (after a stack-overflow crash) "
                "Windows itself went down. Do NOT retry immediately — "
                "check the physical target.\n"
                "  • routeMissing  → Local AMS router has no route to the "
                "target. Setup issue; no retry will help until the route "
                "is configured in TwinCAT System Manager.\n"
            ),
            inputSchema={
                "type": "object",
                "properties": {
                    "amsNetId": {
                        "type": "string",
                        "description": f"Target AMS Net ID. {_AMS_NET_ID_DESC}"
                    },
                    "port": {
                        "type": "integer",
                        "description": "PLC runtime AMS port (default: 851). Only the runtime probe uses this; the system-service probe always uses 10000.",
                        "default": 851
                    },
                    "timeoutMs": {
                        "type": "integer",
                        "description": "Per-probe timeout in milliseconds (default: 2500). Worst-case total = 2 × timeoutMs.",
                        "default": 2500
                    }
                },
                "required": []
            },
            annotations={
                "readOnlyHint": True,
                "destructiveHint": False,
                "idempotentHint": True
            }
        ),
        Tool(
            name="twincat_get_state",
            description="Get the TwinCAT runtime state via direct ADS connection. Does NOT require Visual Studio - connects directly to the PLC. Returns: Run, Stop, Config, Error, etc.",
            inputSchema={
                "type": "object",
                "properties": {
                    "amsNetId": {
                        "type": "string",
                        "description": f"AMS Net ID of the target PLC (e.g., '172.18.236.100.1.1' or '127.0.0.1.1.1' for local). {_AMS_NET_ID_DESC}"
                    },
                    "port": {
                        "type": "integer",
                        "description": "ADS port number (default: 851 for PLC runtime 1)",
                        "default": 851
                    }
                },
                "required": []
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
                        "description": f"AMS Net ID of the target PLC (e.g., '172.18.236.100.1.1'). {_AMS_NET_ID_DESC}"
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
                "required": ["state"]
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
                        "description": f"AMS Net ID of the target PLC (e.g., '172.18.236.100.1.1'). {_AMS_NET_ID_DESC}"
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
                "required": ["symbol"]
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
                        "description": f"AMS Net ID of the target PLC (e.g., '172.18.236.100.1.1'). {_AMS_NET_ID_DESC}"
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
                "required": ["symbol", "value"]
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
            description=(
                "Read the Visual Studio Error List window — this is where "
                "AdsLogStr() output, TcUnit per-test messages ('FAILED TEST "
                "...', 'Test suite ID=...'), and runtime _Raise messages "
                "land, alongside build errors/warnings. Data source: VS's "
                "in-memory error list (same buffer the IDE shows), which "
                "can roll off older items under heavy load — if you need a "
                "persistent stream, pair this with a live session or use "
                "twincat_read_plc_log (ADS logger, target-side).\n\n"
                "Common usage:\n"
                "  • contains='FAILED TEST'    → just TcUnit failures\n"
                "  • contains='E_SM_Fault'     → just your error topic\n"
                "  • includeErrors=false, includeWarnings=false, "
                "contains='...' → runtime ADS messages only\n\n"
                "Default waitSeconds is 2s so async ADS messages have time "
                "to surface. Bump higher (e.g. 5-10s) right after a restart "
                "or an operation you expect to emit logs."
            ),
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
                        "description": "Include messages (ADS logs, TcUnit output, etc.). Default: true",
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
                        "description": (
                            "Wait N seconds before reading so async ADS "
                            "messages have time to arrive. Default: 2."
                        ),
                        "default": 2
                    },
                    "contains": {
                        "type": "string",
                        "description": (
                            "Case-insensitive substring filter applied to "
                            "each item's description. Use this to cut "
                            "through library-reload noise — e.g. "
                            "'FAILED TEST', 'E_SM_Fault', 'stack'. Omit "
                            "to get everything (may be hundreds of items)."
                        )
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
            description=(
                "Run TcUnit tests on a TwinCAT PLC project and return "
                "results. Handles the full test workflow: build, configure "
                "task, set boot project, optionally disable I/O, activate, "
                "restart, and poll for results.\n\n"
                "Returns: test counts (passed/failed/total/suites), "
                "duration, AND a structured `failures` array with one "
                "entry per failed test — each entry has suite, test, "
                "expected, actual, message. No need to scavenge the error "
                "list afterwards for per-test detail; it's already here.\n\n"
                "Post-run caveats:\n"
                "  • While the test task owns the runtime, the symbol "
                "table may not match your normal (non-test) project. If "
                "you try `twincat_read_var` on a non-test symbol and get "
                "`DeviceSymbolNotFound (0x710)`, use `twincat_list_symbols` "
                "with a prefix to see what's actually loaded.\n"
                "  • Runtime reboots into the test configuration during "
                "the run. To get back to the normal config, re-activate "
                "the non-test configuration afterwards."
            ),
            inputSchema={
                "type": "object",
                "properties": {
                    "solutionPath": {
                        "type": "string",
                        "description": "Full path to the TwinCAT .sln file"
                    },
                    "amsNetId": {
                        "type": "string",
                        "description": f"Target AMS Net ID. {_AMS_NET_ID_DESC}"
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
        ),
        Tool(
            name="twincat_set_default_target",
            description=(
                "Change (or clear) the PERSISTENT default PLC target used by every tool "
                "that takes an `amsNetId` (tcunit, deploy, activate, restart, set-target, "
                "get/set state, read/write var). Use this when the user says something like "
                "'always target X from now on' or 'switch to the test rig': the value is "
                "written to the MCP config file (%LOCALAPPDATA%\\twincat-mcp\\config.json) "
                "and survives conversations and server restarts — so a later chat inherits "
                "the same default without the user having to re-explain.\n\n"
                "Modes:\n"
                "  • pass `amsNetId` to set a new persistent default\n"
                "  • pass `reset: true` to remove the persisted value and fall back to the "
                "`TWINCAT_DEFAULT_AMS_NET_ID` env var or the hardcoded localhost default\n"
                "  • pass neither to get a read-only status snapshot of what each source "
                "currently says and which one is active\n\n"
                "Per-call overrides still work: pass `amsNetId` explicitly to any tool to "
                "use a different target for that single call. Not gated by armed mode — "
                "writing config doesn't touch any PLC; destructive tools still have their "
                "own arm + confirm gates."
            ),
            inputSchema={
                "type": "object",
                "properties": {
                    "amsNetId": {
                        "type": "string",
                        "description": (
                            "New persistent default AMS Net ID (e.g., '5.22.157.86.1.1'). "
                            "Must be six dot-separated octets in 0-255. "
                            "Omit together with `reset` to get a read-only status."
                        )
                    },
                    "reset": {
                        "type": "boolean",
                        "description": (
                            "If true, remove the persisted default and fall back to env var / "
                            "hardcoded fallback. Ignored when `amsNetId` is also provided."
                        ),
                        "default": False
                    },
                    "reason": {
                        "type": "string",
                        "description": (
                            "Optional short note saved alongside the value (e.g., "
                            "'switched to the conveyor test rig'). Helps when inspecting "
                            "the config file later."
                        )
                    }
                },
                "required": []
            },
            annotations={
                "readOnlyHint": False,
                "destructiveHint": False,
                "idempotentHint": True
            }
        )
    ]
