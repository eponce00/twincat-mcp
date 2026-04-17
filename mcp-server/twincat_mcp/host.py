"""
Persistent shell host subsystem.

Talks to a long-lived `TcAutomation.exe host` subprocess that owns ONE
TcXaeShell / Visual Studio DTE for the MCP server's entire lifetime.
Per-call shell startup (~25-90s) is paid once per server session instead
of per tool call. Combined with the C# parent-death watchdog +
session-file janitor, phantom TcXaeShell processes are impossible even
across hard crashes.

The host is lazily spawned on the first shell-needing tool call and
torn down via atexit on a clean Python exit (plus defensively by the
host's own parent-death watchdog on crash).

Exports:
  - HOST_DISABLED          environment flag (TWINCAT_DISABLE_HOST=1)
  - HostError              exception type for host failures
  - ShellHost              the subprocess-management class
  - _CIDict, _ci_wrap      case-insensitive dict helpers
  - get_shell_host()       lazy singleton accessor
  - get_shell_host_if_alive()  non-starting accessor used by status/kill tools
  - drop_shell_host()      clear the singleton without starting a new one
  - shutdown_shell_host()  graceful shutdown (idempotent, used by atexit)
"""

import atexit
import json
import os
import queue
import subprocess
import sys
import threading
import time
from pathlib import Path

from .cli import find_tc_automation_exe

# -----------------------------------------------------------------------------
# Environment knobs
# -----------------------------------------------------------------------------

# Set TWINCAT_DISABLE_HOST=1 to force every call through the legacy per-call
# CLI path (useful for isolating host-related issues).
HOST_DISABLED = os.environ.get("TWINCAT_DISABLE_HOST", "").strip() in ("1", "true", "yes")


# -----------------------------------------------------------------------------
# Case-insensitive dict helpers
# -----------------------------------------------------------------------------

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


# -----------------------------------------------------------------------------
# Exception
# -----------------------------------------------------------------------------

class HostError(Exception):
    """Raised when the persistent shell host cannot be used (start failed,
    crashed mid-call, or returned malformed data). Callers should fall back
    to the legacy CLI path."""


# -----------------------------------------------------------------------------
# ShellHost
# -----------------------------------------------------------------------------

def _paths_equal(a: str, b: str) -> bool:
    try:
        na = os.path.normcase(os.path.normpath(os.path.abspath(a)))
        nb = os.path.normcase(os.path.normpath(os.path.abspath(b)))
        return na == nb
    except Exception:
        return a == b


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


# -----------------------------------------------------------------------------
# Module-level singleton + lifecycle helpers
# -----------------------------------------------------------------------------

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


def get_shell_host_if_alive() -> ShellHost | None:
    """
    Return the current ShellHost singleton WITHOUT constructing one.
    Used by status/kill tools that must NOT accidentally spawn a new host
    just to inspect its state.
    """
    return _shell_host


def drop_shell_host() -> None:
    """
    Clear the singleton reference without shutting anything down. Used by
    callers that have already terminated the process externally (e.g.
    twincat_kill_stale, or run_shell_step after detecting a dead host) so
    the next get_shell_host() starts a fresh instance.
    """
    global _shell_host
    _shell_host = None


def shutdown_shell_host() -> None:
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


# Register cleanup for graceful Python exits. Hard crashes are handled by
# the host's parent-death watchdog + session-file janitor (see
# Core/SessionFile.cs on the C# side).
atexit.register(shutdown_shell_host)
