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

import atexit
import json
import os
import queue
import subprocess
import sys
import tempfile
import threading
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

# Low-level (C# CLI) batch step commands that count as dangerous when used
# inside twincat_batch. If any step in a batch matches one of these, the batch
# as a whole is treated as dangerous and requires armed mode.
DANGEROUS_BATCH_COMMANDS = {
    "activate",
    "restart",
    "set-state",
    "write-var",
}

# Low-level batch step commands that also require an explicit confirm='CONFIRM'
# at the batch level (same policy as twincat_activate / twincat_restart).
CONFIRMATION_REQUIRED_BATCH_COMMANDS = {
    "activate",
    "restart",
}

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
            timeout=120,  # 2 minute timeout
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
            "errorMessage": "Command timed out after 2 minutes"
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
            try:
                for line in iter(process.stderr.readline, ''):
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
        
        # Wait for process with timeout
        # Add 3 min overhead for VS startup + activation + restart + sleeps
        # on top of the user-requested poll timeout
        timeout_seconds = timeout_minutes * 60 + 180
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


# =============================================================================
# PERSISTENT SHELL HOST
# =============================================================================
#
# Talks to a long-lived `TcAutomation.exe host` subprocess that owns ONE
# TcXaeShell / Visual Studio DTE for the MCP server's entire lifetime. Per-call
# shell startup (~25-90s) is paid once per server session instead of per tool
# call. Combined with the C# parent-death watchdog + session-file janitor,
# phantom TcXaeShell processes are impossible even across hard crashes.
#
# The host is lazily spawned on the first shell-needing tool call and torn
# down via atexit on a clean Python exit (plus defensively by the host's own
# parent-death watchdog on crash).
# =============================================================================


class _CIDict(dict):
    """
    Dict subclass whose .get() falls back to a case-insensitive key match.
    Used to preserve compatibility with tool handlers that read response
    fields using either PascalCase (`result.get("Success")`) or camelCase
    (`result.get("success")`). Legacy CLI paths return PascalCase for some
    commands; host-routed responses are uniformly camelCase. Wrapping both
    paths with this lets every existing formatter keep working unchanged.
    """

    def get(self, key, default=None):
        if key in self:
            return super().__getitem__(key)
        try:
            low = {k.lower(): k for k in self.keys() if isinstance(k, str)}
            real = low.get(key.lower()) if isinstance(key, str) else None
        except Exception:
            real = None
        if real is not None:
            return super().__getitem__(real)
        return default


def _ci_wrap(obj):
    """Recursively wrap dicts so .get() is case-insensitive."""
    if isinstance(obj, dict):
        return _CIDict({k: _ci_wrap(v) for k, v in obj.items()})
    if isinstance(obj, list):
        return [_ci_wrap(x) for x in obj]
    return obj


# Environment override: set TWINCAT_DISABLE_HOST=1 to force every call through
# the legacy per-call CLI path (useful for isolating host-related issues).
HOST_DISABLED = os.environ.get("TWINCAT_DISABLE_HOST", "").strip() in ("1", "true", "yes")


class HostError(Exception):
    """Raised when the persistent shell host cannot be used (start failed,
    crashed mid-call, or returned malformed data). Callers should fall back
    to the legacy CLI path."""


class ShellHost:
    """
    Manages the lifecycle of a `TcAutomation.exe host` subprocess and
    dispatches JSON-RPC calls to it over NDJSON on stdin/stdout.

    Concurrency model: one DTE is STA-affine, so all calls are serialized
    through a single lock. Progress messages from stderr are captured per
    call (fenced by request-id boundaries) and returned alongside the result.

    Lifecycle:
      - First `ensure_solution()` / `call()` lazily starts the subprocess.
      - `shutdown()` sends the graceful shutdown request and waits.
      - atexit + signal handlers trigger shutdown on a clean exit.
      - On a hard crash, the host's own parent-death watchdog takes over.
    """

    # Time to wait for the "ready" handshake line on startup.
    READY_TIMEOUT_SEC = 30.0

    def __init__(self, exe_path: Path):
        self._exe_path = exe_path
        self._proc: subprocess.Popen | None = None
        self._lock = threading.Lock()
        self._stdout_thread: threading.Thread | None = None
        self._stderr_thread: threading.Thread | None = None
        self._responses: "queue.Queue[dict]" = queue.Queue()
        self._progress: list[str] = []
        self._progress_lock = threading.Lock()
        self._request_id = 0
        self._current_solution: str | None = None
        self._current_tc_version: str | None = None
        self._ready_info: dict | None = None
        self._last_error: str | None = None

    # ---------------- public API ----------------

    def is_alive(self) -> bool:
        return self._proc is not None and self._proc.poll() is None

    def status(self) -> dict:
        """Query the host for its own status. Starts the host if needed."""
        if not self.is_alive():
            self._ensure_started()
        return self._call_raw("status", None, timeout=10)

    def ensure_solution(self, solution_path: str, tc_version: str | None,
                        timeout: float = 120.0) -> dict:
        """
        Ensure the host has the given solution loaded. Lazily starts the
        host and/or reloads a different solution.
        """
        if HOST_DISABLED:
            raise HostError("host disabled via TWINCAT_DISABLE_HOST")

        if not solution_path:
            raise HostError("ensure_solution requires a solution path")

        with self._lock:
            if not self.is_alive():
                self._start_locked()

            # Cheap idempotency: if already pointing at the same solution
            # skip the round-trip. Comparison is normalized.
            if self._current_solution and _paths_equal(self._current_solution, solution_path):
                if (tc_version or None) == (self._current_tc_version or None):
                    return {"loaded": True, "cached": True, "solutionPath": solution_path}

            params = {"solutionPath": solution_path}
            if tc_version:
                params["tcVersion"] = tc_version
            res = self._call_raw_locked("ensure-solution", params, timeout=timeout)
            self._current_solution = solution_path
            self._current_tc_version = tc_version
            return res

    def execute_step(self, command: str, step_args: dict,
                     solution_path: str | None, tc_version: str | None,
                     timeout: float = 600.0) -> tuple[dict, list[str]]:
        """
        Run a single StepDispatcher command in the host's DTE.
        Returns (inner_result_dict, progress_messages).
        """
        if HOST_DISABLED:
            raise HostError("host disabled via TWINCAT_DISABLE_HOST")

        # Only shell commands need a loaded solution; ADS commands don't.
        shell_commands = {
            "build", "info", "clean", "set-target", "activate", "restart",
            "list-plcs", "set-boot-project", "disable-io", "set-variant",
            "list-tasks", "configure-task", "configure-rt",
            "check-all-objects", "static-analysis", "generate-library",
            "get-error-list",
            "deploy", "run-tcunit",
        }
        if command in shell_commands:
            if not solution_path:
                raise HostError(f"{command} requires a solution path")
            self.ensure_solution(solution_path, tc_version, timeout=120.0)

        with self._lock:
            if not self.is_alive():
                self._start_locked()

            # Drain per-call progress buffer so we only attribute new lines
            # to THIS call. Progress lines that arrived between calls get
            # discarded (they would belong to the previous one).
            with self._progress_lock:
                self._progress.clear()

            params = {"command": command, "args": step_args or {}}
            resp = self._call_raw_locked("execute-step", params, timeout=timeout)

            # HandleExecuteStep wraps: {command, result: <inner>}
            # We want the inner command result.
            inner = resp.get("result") if isinstance(resp, dict) else None
            if inner is None:
                inner = resp

            with self._progress_lock:
                progress = list(self._progress)

            return inner, progress

    def shutdown(self, timeout: float = 8.0):
        """Politely ask the host to shut down; force-kill if it won't."""
        with self._lock:
            if not self.is_alive():
                return
            try:
                self._send_request("shutdown", None)
            except Exception:
                pass

            end = time.time() + timeout
            while time.time() < end and self.is_alive():
                time.sleep(0.1)

            if self.is_alive():
                try:
                    self._proc.kill()  # type: ignore
                except Exception:
                    pass

            self._cleanup_locked()

    # ---------------- internals ----------------

    def _ensure_started(self):
        with self._lock:
            if not self.is_alive():
                self._start_locked()

    def _start_locked(self):
        if self.is_alive():
            return

        cmd = [str(self._exe_path), "host", "--mcp-pid", str(os.getpid())]
        try:
            self._proc = subprocess.Popen(
                cmd,
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                encoding="utf-8",
                bufsize=1,
                cwd=str(self._exe_path.parent),
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
        except Exception as e:
            self._proc = None
            raise HostError(f"failed to spawn host: {e}")

        # Kick off stream drainers before any other interaction; otherwise
        # the pipe buffers can fill and deadlock on long runs.
        self._stdout_thread = threading.Thread(
            target=self._stdout_loop, name="ShellHostStdout", daemon=True)
        self._stderr_thread = threading.Thread(
            target=self._stderr_loop, name="ShellHostStderr", daemon=True)
        self._stdout_thread.start()
        self._stderr_thread.start()

        # Wait for the ready line (the very first message the host emits).
        deadline = time.time() + self.READY_TIMEOUT_SEC
        while time.time() < deadline:
            if not self.is_alive():
                err = self._last_error or "host exited during startup"
                raise HostError(err)
            if self._ready_info is not None:
                return
            time.sleep(0.05)

        try: self._proc.kill()
        except Exception: pass
        raise HostError("timed out waiting for host 'ready' line")

    def _cleanup_locked(self):
        self._proc = None
        self._stdout_thread = None
        self._stderr_thread = None
        self._ready_info = None
        self._current_solution = None
        self._current_tc_version = None
        while not self._responses.empty():
            try: self._responses.get_nowait()
            except Exception: break

    def _next_id(self) -> int:
        self._request_id += 1
        return self._request_id

    def _send_request(self, method: str, params: dict | None) -> int:
        if not self.is_alive():
            raise HostError("host process is not running")
        req_id = self._next_id()
        payload = {"id": req_id, "method": method}
        if params is not None:
            payload["params"] = params
        line = json.dumps(payload, separators=(",", ":"))
        try:
            self._proc.stdin.write(line + "\n")  # type: ignore
            self._proc.stdin.flush()  # type: ignore
        except (BrokenPipeError, OSError) as e:
            raise HostError(f"failed to write to host stdin: {e}")
        return req_id

    def _call_raw(self, method: str, params: dict | None, timeout: float) -> dict:
        with self._lock:
            return self._call_raw_locked(method, params, timeout)

    def _call_raw_locked(self, method: str, params: dict | None, timeout: float) -> dict:
        req_id = self._send_request(method, params)
        deadline = time.time() + timeout
        while True:
            remaining = deadline - time.time()
            if remaining <= 0:
                raise HostError(f"{method} timed out after {timeout:.0f}s")
            try:
                msg = self._responses.get(timeout=min(remaining, 0.5))
            except queue.Empty:
                if not self.is_alive():
                    raise HostError(self._last_error or "host exited during call")
                continue

            if not isinstance(msg, dict):
                continue
            if msg.get("id") != req_id:
                # Out-of-order or unsolicited; drop with a note.
                continue

            if msg.get("ok"):
                return msg.get("result", {})
            else:
                err = msg.get("error") or "host returned error"
                raise HostError(str(err))

    def _stdout_loop(self):
        try:
            proc = self._proc
            if proc is None or proc.stdout is None:
                return
            for line in proc.stdout:
                line = line.strip()
                if not line:
                    continue
                try:
                    msg = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if isinstance(msg, dict) and msg.get("type") == "ready":
                    self._ready_info = msg
                    continue
                self._responses.put(msg)
        except Exception:
            pass

    def _stderr_loop(self):
        try:
            proc = self._proc
            if proc is None or proc.stderr is None:
                return
            for line in proc.stderr:
                line = line.rstrip()
                if not line:
                    continue
                if line.startswith("[PROGRESS]"):
                    clean = line[len("[PROGRESS]"):].strip()
                    with self._progress_lock:
                        self._progress.append(clean)
                else:
                    # Retain a breadcrumb for diagnostics; the latest stderr
                    # line is surfaced in HostError messages when the host
                    # dies unexpectedly.
                    self._last_error = line
        except Exception:
            pass


def _paths_equal(a: str, b: str) -> bool:
    try:
        na = os.path.normcase(os.path.normpath(os.path.abspath(a)))
        nb = os.path.normcase(os.path.normpath(os.path.abspath(b)))
        return na == nb
    except Exception:
        return a == b


# Module-level singleton + lazy accessor.
_shell_host: ShellHost | None = None
_shell_host_init_lock = threading.Lock()


def get_shell_host() -> ShellHost | None:
    """Return the shared ShellHost, constructing it on first use.
    Returns None if the host is disabled or the exe cannot be found."""
    global _shell_host
    if HOST_DISABLED:
        return None
    if _shell_host is not None:
        return _shell_host
    with _shell_host_init_lock:
        if _shell_host is None:
            try:
                exe = find_tc_automation_exe()
            except Exception:
                return None
            _shell_host = ShellHost(exe)
    return _shell_host


def shutdown_shell_host():
    """Tear down the persistent host. Idempotent; safe to call from atexit."""
    global _shell_host
    host = _shell_host
    if host is None:
        return
    try:
        host.shutdown(timeout=8.0)
    except Exception:
        pass
    _shell_host = None


# Register cleanup for graceful Python exits. Hard crashes are handled by the
# host's parent-death watchdog + session-file janitor (see Core/SessionFile.cs
# in the C# side).
atexit.register(shutdown_shell_host)


def run_shell_step(
    command: str,
    step_args: dict | None,
    solution_path: str | None = None,
    tc_version: str | None = None,
    timeout_minutes: int = 10,
) -> tuple[dict, list[str]]:
    """
    Run one TcAutomation command, preferring the persistent shell host.
    Falls back to spawning a single-step batch via the CLI if the host is
    unavailable, unhealthy, or explicitly disabled.

    Returns (result_dict, progress_messages). The result dict is wrapped in
    a _CIDict so existing tool handlers can read PascalCase OR camelCase
    keys without change.
    """
    step_args = step_args or {}

    host = get_shell_host()
    if host is not None:
        try:
            inner, progress = host.execute_step(
                command, step_args, solution_path, tc_version,
                timeout=timeout_minutes * 60 + 180,
            )
            return _ci_wrap(inner), progress
        except HostError as e:
            # Log once to stderr and fall through to CLI. Subsequent calls
            # will re-attempt host; this matters if the host crashed but
            # can be restarted.
            sys.stderr.write(f"[mcp-server] shell host unavailable ({e}); falling back to CLI\n")
            sys.stderr.flush()
            # If the process died, drop the stale instance so the next call
            # gets a fresh start attempt.
            if not host.is_alive():
                global _shell_host
                _shell_host = None

    # --- CLI fallback: spawn a single-step batch ---------------------------
    # We reuse the existing batch CLI to avoid having to build per-command
    # flag construction for every tool. One batch step = one tool call.
    batch_input: dict = {
        "stopOnError": True,
        "steps": [{"command": command, "args": step_args}],
    }
    if solution_path:
        batch_input["solutionPath"] = solution_path
    if tc_version:
        batch_input["tcVersion"] = tc_version

    tmp = tempfile.NamedTemporaryFile(
        mode="w", suffix=".json", prefix="tc-step-", delete=False, encoding="utf-8"
    )
    try:
        json.dump(batch_input, tmp)
        tmp.flush()
        tmp.close()
        batch_result, progress = run_tc_automation_with_progress(
            "batch", ["--input", tmp.name], timeout_minutes
        )
    finally:
        try: os.unlink(tmp.name)
        except Exception: pass

    # Unwrap: batch_result.results[0].result is the inner command result.
    results = batch_result.get("results") or []
    if results:
        first = results[0] if isinstance(results[0], dict) else {}
        if first.get("success"):
            inner = first.get("result") or {}
            return _ci_wrap(inner), progress
        # Failure — synthesize an error-shaped result that works with
        # both PascalCase and camelCase handler patterns.
        err_msg = first.get("error") or batch_result.get("errorMessage") or "Step failed"
        return _ci_wrap({
            "success": False,
            "Success": False,
            "errorMessage": err_msg,
            "ErrorMessage": err_msg,
            "error": err_msg,
        }), progress

    # Batch itself failed before the step ran.
    err_msg = batch_result.get("errorMessage") or "Batch dispatch failed"
    return _ci_wrap({
        "success": False,
        "Success": False,
        "errorMessage": err_msg,
        "ErrorMessage": err_msg,
        "error": err_msg,
    }), progress


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

        host = _shell_host  # read without triggering lazy start
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
            globals()["_shell_host"] = None

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
        host = _shell_host
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
