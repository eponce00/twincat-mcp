"""A bounded, owned Scope subprocess. No implicit restart for stop/status."""

import json
import queue
import subprocess
import threading

from .cli import find_tc_automation_exe


class ScopeSession:
    def __init__(self):
        self.process = None
        self.lines = queue.Queue()
        self.lock = threading.RLock()

    @property
    def is_running(self):
        return self.process is not None and self.process.poll() is None

    def _read(self, timeout):
        try:
            line = self.lines.get(timeout=timeout)
            if not line:
                raise RuntimeError("Scope process ended unexpectedly.")
            return json.loads(line)
        except Exception:
            self.close(force=True)
            raise

    def start(self):
        with self.lock:
            if self.is_running:
                raise RuntimeError("A Scope session is already active.")
            exe = find_tc_automation_exe()
            self.lines = queue.Queue()
            self.process = subprocess.Popen(
                [str(exe), "scope-session"],
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                encoding="utf-8",
                cwd=str(exe.parent),
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
            proc = self.process

            def read_stdout():
                for line in proc.stdout:
                    self.lines.put(line)
                self.lines.put("")

            def drain_stderr():
                for _ in proc.stderr:
                    pass

            threading.Thread(target=read_stdout, daemon=True).start()
            threading.Thread(target=drain_stderr, daemon=True).start()
            ready = self._read(30)
            if not ready.get("success"):
                self.close(force=True)
                raise RuntimeError(ready.get("errorMessage", "Scope startup failed."))

    def send_command(self, command, timeout_seconds=60):
        with self.lock:
            if not self.is_running:
                raise RuntimeError("Scope session expired. No new process was started.")
            self.process.stdin.write(json.dumps(command) + "\n")
            self.process.stdin.flush()
            return self._read(timeout_seconds)

    def close(self, force=False):
        with self.lock:
            proc = self.process
            if proc is None:
                return
            try:
                if proc.poll() is None:
                    if not force:
                        proc.stdin.write('{"command":"exit"}\n')
                        proc.stdin.flush()
                        proc.wait(timeout=5)
                    else:
                        proc.kill()
            except Exception:
                if proc.poll() is None:
                    proc.kill()
            finally:
                try:
                    proc.wait(timeout=5)
                except Exception:
                    pass
                for stream in (proc.stdin, proc.stdout, proc.stderr):
                    if stream:
                        stream.close()
                self.process = None
