"""Bound stdio server lifetime to its client, including Windows venv redirectors."""

import ctypes
import os
import sys
import threading

_active = None


class WindowsProcess:
    def __init__(self, pid):
        from ctypes import wintypes

        self.pid = pid
        self.api = ctypes.WinDLL("kernel32", use_last_error=True)
        self.api.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        self.api.OpenProcess.restype = wintypes.HANDLE
        self.api.CloseHandle.argtypes = [wintypes.HANDLE]
        self.api.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        self.api.WaitForSingleObject.restype = wintypes.DWORD
        self.api.QueryFullProcessImageNameW.argtypes = [
            wintypes.HANDLE,
            wintypes.DWORD,
            wintypes.LPWSTR,
            ctypes.POINTER(wintypes.DWORD),
        ]
        self.api.GetProcessTimes.argtypes = [wintypes.HANDLE] + [
            ctypes.POINTER(wintypes.FILETIME)
        ] * 4
        self.handle = self.api.OpenProcess(0x1000 | 0x100000, False, pid)
        if not self.handle:
            raise OSError(ctypes.get_last_error(), "Cannot open client process")

    def alive(self):
        state = self.api.WaitForSingleObject(self.handle, 0)
        if state == 0xFFFFFFFF:
            raise OSError(ctypes.get_last_error(), "Cannot query client process")
        return state == 0x102

    def image(self):
        from ctypes import wintypes

        size = wintypes.DWORD(32768)
        text = ctypes.create_unicode_buffer(size.value)
        if not self.api.QueryFullProcessImageNameW(self.handle, 0, text, ctypes.byref(size)):
            raise OSError(ctypes.get_last_error(), "Cannot read process image")
        return os.path.normcase(os.path.abspath(text.value))

    def created(self):
        from ctypes import wintypes

        values = [wintypes.FILETIME() for _ in range(4)]
        if not self.api.GetProcessTimes(self.handle, *[ctypes.byref(value) for value in values]):
            raise OSError(ctypes.get_last_error(), "Cannot read process identity")
        return (values[0].dwHighDateTime << 32) | values[0].dwLowDateTime

    def parent_pid(self):
        # PROCESS_BASIC_INFORMATION uses pointer-sized fields on both architectures.
        class Basic(ctypes.Structure):
            _fields_ = [
                ("exit", ctypes.c_void_p),
                ("peb", ctypes.c_void_p),
                ("affinity", ctypes.c_void_p),
                ("priority", ctypes.c_void_p),
                ("pid", ctypes.c_void_p),
                ("parent", ctypes.c_void_p),
            ]

        query = ctypes.WinDLL("ntdll").NtQueryInformationProcess
        query.argtypes = [
            ctypes.c_void_p,
            ctypes.c_ulong,
            ctypes.c_void_p,
            ctypes.c_ulong,
            ctypes.c_void_p,
        ]
        query.restype = ctypes.c_long
        info = Basic()
        if query(self.handle, 0, ctypes.byref(info), ctypes.sizeof(info), None) != 0:
            raise OSError("Cannot resolve launcher parent")
        return int(info.parent or 0)

    def close(self):
        if self.handle:
            self.api.CloseHandle(self.handle)
            self.handle = None


def resolve_windows_client(factory=WindowsProcess):
    direct = factory(os.getppid())
    try:
        # The redirector's image is sys.executable, while the running interpreter
        # uses the base image. Watching only the redirector misses client death:
        # the redirector itself waits for this process to exit.
        own = factory(os.getpid())
        try:
            redirector = (
                sys.prefix != sys.base_prefix
                and direct.image() == os.path.normcase(os.path.abspath(sys.executable))
                and own.image() != direct.image()
            )
            if redirector:
                client = factory(direct.parent_pid())
                if client.created() > direct.created():
                    client.close()
                    raise OSError("Client PID was reused before identity could be established")
                direct.close()
                return client
        finally:
            own.close()
        return direct
    except BaseException:
        direct.close()
        raise


class ParentWatchdog:
    def __init__(self, on_lost, grace=60, parent=None, exit_process=os._exit):
        self.stop = threading.Event()
        self.finished = threading.Event()
        self.on_lost = on_lost
        self.grace = grace
        self.exit_process = exit_process
        self.parent = parent
        self.initial_parent = os.getppid()
        self.client_pid = None
        self.enabled = False
        self.thread = None

    def start(self):
        global _active
        if os.environ.get("TWINCAT_MCP_PARENT_WATCHDOG", "1") == "0":
            return
        if self.parent is None and os.name == "nt":
            try:
                self.parent = resolve_windows_client()
            except OSError as exc:
                print(f"Client watchdog unavailable: {exc}", file=sys.stderr)
                return
        self.client_pid = self.parent.pid if self.parent else self.initial_parent
        self.enabled = True
        _active = self
        self.thread = threading.Thread(target=self._run, name="mcp-client-watchdog", daemon=True)
        self.thread.start()

    def _run(self):
        try:
            while not self.stop.wait(0.2):
                try:
                    alive = (
                        self.parent.alive() if self.parent else os.getppid() == self.initial_parent
                    )
                except OSError:
                    # An unverified identity is not evidence of client death.
                    continue
                if not alive:
                    print("MCP client exited; closing server.", file=sys.stderr)
                    self.on_lost()
                    if not self.finished.wait(self.grace):
                        # A stuck stdio reader can retain an inherited write end.
                        # Exit only this process; native children own their cleanup.
                        self.exit_process(0)
                    return
        finally:
            if self.parent:
                self.parent.close()

    def close(self):
        self.stop.set()
        # Keep the hard deadline armed until normal interpreter exit if client
        # death has already started shutdown; asyncio may be waiting on stdin IO.
        if self.thread and self.thread.is_alive():
            self.thread.join(timeout=0.3)


def status():
    return dict(
        processId=os.getpid(),
        parentPid=os.getppid(),
        monitoredClientPid=_active.client_pid if _active else None,
        parentWatchdogEnabled=bool(_active and _active.enabled),
    )
