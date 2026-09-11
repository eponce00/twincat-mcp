"""Bounded one-shot CLI adapters for recording, export and orphan cleanup."""

import json
import os
import subprocess
from pathlib import Path

# -----------------------------------------------------------------------------
# Executable discovery
# -----------------------------------------------------------------------------

# Resolve relative to this file: <repo>/mcp-server/twincat_mcp/cli.py
# -> parent parent parent == <repo>
_PACKAGE_DIR = Path(__file__).parent  # .../mcp-server/twincat_mcp
_MCP_SERVER_DIR = _PACKAGE_DIR.parent  # .../mcp-server
_REPO_ROOT = _MCP_SERVER_DIR.parent  # .../twincat-mcp

TC_AUTOMATION_PATHS = [
    _REPO_ROOT / "TcAutomation" / "bin" / "Release-v2" / "TcAutomation.exe",
    _REPO_ROOT / "TcAutomation" / "bin" / "Debug" / "TcAutomation.exe",
    _REPO_ROOT / "TcAutomation" / "publish" / "TcAutomation.exe",
]


def find_tc_automation_exe() -> Path:
    """Find the TcAutomation.exe executable, raising FileNotFoundError with a helpful message."""
    configured = os.environ.get("TWINCAT_AUTOMATION_EXE")
    if configured:
        path = Path(configured).resolve()
        if not path.is_file():
            raise FileNotFoundError(f"Configured TWINCAT_AUTOMATION_EXE does not exist: {path}")
        return path
    for path in TC_AUTOMATION_PATHS:
        if path.exists():
            return path
    raise FileNotFoundError(
        "TcAutomation.exe not found. Searched paths:\n"
        + "\n".join(f"  - {p}" for p in TC_AUTOMATION_PATHS)
        + "\n\nPlease build the TcAutomation project first:\n"
        + "  .\\scripts\\build.ps1"
    )


def run_tc_automation(command: str, args: list[str]) -> dict:
    result, _ = _run(command, args, 120)
    return result


def run_tc_automation_with_progress(command: str, args: list[str], timeout_minutes: int = 10):
    return _run(command, args, timeout_minutes * 60 + 180)


def _run(command, args, timeout):
    exe = find_tc_automation_exe()
    try:
        proc = subprocess.Popen(
            [str(exe), command] + args,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            cwd=str(exe.parent),
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
    except OSError as exc:
        return dict(success=False, dispatched=False, errorMessage=str(exc)), []
    try:
        stdout, stderr = proc.communicate(timeout=timeout)
    except subprocess.TimeoutExpired:
        proc.kill()
        proc.communicate()
        return dict(
            success=False, outcomeUnknown=True, errorMessage="CLI timed out; no replay occurred."
        ), []
    progress = [line.removeprefix("[PROGRESS]").strip()[:1000] for line in stderr.splitlines()][
        -50:
    ]
    try:
        result = json.loads(stdout)
        if not isinstance(result, dict):
            raise ValueError("Expected result object")
    except (ValueError, TypeError):
        return dict(
            success=False,
            outcomeUnknown=True,
            errorMessage="CLI returned no valid result object.",
            stderr=stderr[-8000:],
        ), progress
    if proc.returncode != 0 and result.get("success", result.get("Success", True)):
        result.update(success=False, errorMessage=f"CLI exited with code {proc.returncode}")
    return result, progress
