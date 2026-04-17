"""
TcAutomation.exe discovery and one-shot subprocess wrappers.

These functions are the "CLI path" for talking to the C# binary:
  - `find_tc_automation_exe`: locate the built executable.
  - `run_tc_automation`: capture stdout+stderr, return parsed JSON.
  - `run_tc_automation_with_progress`: Popen-based runner that streams
    [PROGRESS] lines off stderr while the command runs (used by the batch
    path and as a CLI fallback when the persistent host is unavailable).

These wrappers are legacy as the primary call path, but they stay in use:
  * `twincat_batch` always shells out to `TcAutomation.exe batch`.
  * `run_shell_step` falls back here if the persistent host is down.
  * `twincat_kill_stale` shells out to `TcAutomation.exe reap-orphans`.
"""

import json
import queue
import subprocess
import threading
from pathlib import Path

# -----------------------------------------------------------------------------
# Executable discovery
# -----------------------------------------------------------------------------

# Resolve relative to this file: <repo>/mcp-server/twincat_mcp/cli.py
# -> parent parent parent == <repo>
_PACKAGE_DIR = Path(__file__).parent                 # .../mcp-server/twincat_mcp
_MCP_SERVER_DIR = _PACKAGE_DIR.parent                 # .../mcp-server
_REPO_ROOT = _MCP_SERVER_DIR.parent                   # .../twincat-mcp

TC_AUTOMATION_PATHS = [
    # .NET Framework 4.7.2 build output (current)
    _REPO_ROOT / "TcAutomation" / "bin" / "Release" / "TcAutomation.exe",
    _REPO_ROOT / "TcAutomation" / "bin" / "Debug" / "TcAutomation.exe",
    # Legacy .NET 8 paths (in case someone builds with that)
    _REPO_ROOT / "TcAutomation" / "bin" / "Release" / "net8.0-windows" / "TcAutomation.exe",
    _REPO_ROOT / "TcAutomation" / "publish" / "TcAutomation.exe",
]


def find_tc_automation_exe() -> Path:
    """Find the TcAutomation.exe executable, raising FileNotFoundError with a helpful message."""
    for path in TC_AUTOMATION_PATHS:
        if path.exists():
            return path
    raise FileNotFoundError(
        "TcAutomation.exe not found. Searched paths:\n"
        + "\n".join(f"  - {p}" for p in TC_AUTOMATION_PATHS)
        + "\n\nPlease build the TcAutomation project first:\n"
        + "  .\\scripts\\build.ps1"
    )


# -----------------------------------------------------------------------------
# One-shot subprocess runners
# -----------------------------------------------------------------------------

def run_tc_automation(command: str, args: list[str]) -> dict:
    """
    Run TcAutomation.exe with the given command and arguments. Waits up
    to 2 minutes. Returns the parsed JSON result, or a synthetic
    {"success": False, "errorMessage": ...} dict on failure.
    """
    exe_path = find_tc_automation_exe()
    cmd = [str(exe_path), command] + args

    try:
        result = subprocess.run(
            cmd,
            capture_output=True,
            text=True,
            timeout=120,
            cwd=str(exe_path.parent),
        )

        if result.stdout.strip():
            try:
                return json.loads(result.stdout)
            except json.JSONDecodeError:
                return {
                    "success": False,
                    "errorMessage": f"Invalid JSON output: {result.stdout}",
                    "stderr": result.stderr,
                }
        else:
            return {
                "success": False,
                "errorMessage": result.stderr or "No output from TcAutomation.exe",
            }

    except subprocess.TimeoutExpired:
        return {
            "success": False,
            "errorMessage": "Command timed out after 2 minutes",
        }
    except Exception as e:
        return {
            "success": False,
            "errorMessage": str(e),
        }


def run_tc_automation_with_progress(
    command: str,
    args: list[str],
    timeout_minutes: int = 10,
) -> tuple[dict, list[str]]:
    """
    Run TcAutomation.exe and stream [PROGRESS] lines off stderr while the
    command runs. Returns (result_dict, progress_messages).

    Timeout: `timeout_minutes * 60 + 180` seconds. The extra 3 min covers
    VS startup / activate / restart sleeps that are always present even
    for very short user-requested timeouts.
    """
    exe_path = find_tc_automation_exe()
    cmd = [str(exe_path), command] + args
    progress_messages: list[str] = []

    try:
        process = subprocess.Popen(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            cwd=str(exe_path.parent),
        )

        stderr_queue: "queue.Queue[str]" = queue.Queue()

        def read_stderr():
            try:
                for line in iter(process.stderr.readline, ""):
                    if line:
                        stderr_queue.put(line.strip())
            except ValueError:
                pass  # stderr closed before thread finished reading
            finally:
                try:
                    process.stderr.close()
                except Exception:
                    pass

        stderr_thread = threading.Thread(target=read_stderr, daemon=True)
        stderr_thread.start()

        timeout_seconds = timeout_minutes * 60 + 180
        try:
            stdout, _ = process.communicate(timeout=timeout_seconds)
        except subprocess.TimeoutExpired:
            process.kill()
            return {
                "success": False,
                "errorMessage": f"Command timed out after {timeout_minutes} minutes",
            }, progress_messages

        while not stderr_queue.empty():
            try:
                line = stderr_queue.get_nowait()
                if line.startswith("[PROGRESS]"):
                    progress_messages.append(line[10:].strip())
                else:
                    progress_messages.append(line)
            except queue.Empty:
                break

        if stdout.strip():
            try:
                result = json.loads(stdout)
                result["progressMessages"] = progress_messages
                return result, progress_messages
            except json.JSONDecodeError:
                return {
                    "success": False,
                    "errorMessage": f"Invalid JSON output: {stdout}",
                    "progressMessages": progress_messages,
                }, progress_messages
        else:
            return {
                "success": False,
                "errorMessage": "No output from TcAutomation.exe",
                "progressMessages": progress_messages,
            }, progress_messages

    except Exception as e:
        return {
            "success": False,
            "errorMessage": str(e),
            "progressMessages": progress_messages,
        }, progress_messages
