"""Application orchestration independent of transport and SDK types."""

import os
import secrets
import threading
import time
from pathlib import Path

from .backend import Backend
from .catalog import Catalog
from .errors import OperationError
from .jobs import Jobs
from .safety import Grants

VERSION = "2.0.0"


class Engine:
    def __init__(self, state_dir=None, backend=None):
        self.catalog = Catalog()
        self.backend = backend or Backend()
        self.grants = Grants()
        self.context = None
        self.recording = None
        self.lock = threading.RLock()
        root = Path(
            state_dir
            or os.environ.get("TWINCAT_MCP_STATE_DIR")
            or Path(os.environ.get("LOCALAPPDATA", str(Path.home()))) / "twincat-mcp"
        )
        self.jobs = Jobs(root / "jobs.sqlite3", self.run)

    def context_for(self, handle):
        ctx = self.context
        if not ctx or ctx["handle"] != handle or ctx["invalid"]:
            raise OperationError(
                "expired_context",
                "Unknown or expired engineering context. Close an expired context before opening a replacement.",
            )
        if ctx["started"] and not self.backend.alive():
            ctx["invalid"] = True
            raise OperationError(
                "worker_lost", "Engineering worker lost; no replacement shell was opened."
            )
        return ctx

    def execute(self, operation, arguments, request_key=None):
        args = self.catalog.validate(operation, arguments)
        if operation == "safety.revoke":
            return dict(success=True, **self.grants.revoke(args["grantHandle"]))
        if operation == "job.get":
            return self.jobs.get(args["jobHandle"])
        if operation == "job.result":
            return self.jobs.result(args["jobHandle"], args["offset"], args["limit"])
        if operation == "job.cancel":
            return self.jobs.cancel(args["jobHandle"])
        if operation == "system.status":
            return dict(
                success=True,
                version=VERSION,
                operations=len(self.catalog.entries),
                queued=self.jobs.pending.qsize(),
                protocol="2026-07-28",
            )
        if operation == "context.status":
            with self.lock:
                ctx = self.context
                if not ctx or ctx["handle"] != args["contextHandle"]:
                    raise OperationError("expired_context", "Unknown engineering context.")
                return dict(
                    success=True,
                    solutionPath=ctx["solutionPath"],
                    amsNetId=ctx["amsNetId"],
                    plcName=ctx["plcName"],
                    valid=not ctx["invalid"],
                    workerAlive=self.backend.alive(),
                )
        if not request_key:
            raise OperationError(
                "request_key_required",
                "Submitted work requires requestKey. Reuse it and identical arguments after a lost response.",
            )
        # Deduplication precedes authorization: even expired grants must retrieve
        # the original receipt rather than causing a mutation to run again.
        return self.jobs.submit(operation, args, request_key)

    def preflight(self, operation, args):
        entry = self.catalog.get(operation)
        if operation == "workflow.sequence":
            for step in args["steps"]:
                self.preflight(step["operation"], step["arguments"])
            return
        if entry["requiresGrant"]:
            self.grants.check(operation, args)
        if entry["engineering"]:
            with self.lock:
                self.context_for(args["contextHandle"])
        if operation == "scope.start" and self.recording:
            raise OperationError(
                "recording_busy", "Stop the existing recording using its handle first."
            )
        if operation in {"scope.stop", "scope.status"} and (
            not self.recording or args["recordingHandle"] != self.recording
        ):
            raise OperationError(
                "expired_recording", "Unknown recording handle; no process was started."
            )

    def control(self, operation, args):
        if operation == "context.open":
            with self.lock:
                if self.context:
                    raise OperationError(
                        "context_busy",
                        "An exclusive engineering context exists; its owner must close it.",
                    )
                path = Path(args["solutionPath"])
                if not path.is_absolute() or not path.is_file() or path.suffix.lower() != ".sln":
                    raise OperationError(
                        "invalid_solution", "solutionPath must name an existing absolute .sln file."
                    )
                handle = secrets.token_urlsafe(32)
                self.context = dict(
                    args,
                    solutionPath=str(path.resolve()),
                    handle=handle,
                    started=False,
                    invalid=False,
                )
                return dict(
                    success=True,
                    contextHandle=handle,
                    target=args["amsNetId"],
                    plcName=args["plcName"],
                )
        if operation == "context.close":
            with self.lock:
                if not self.context or self.context["handle"] != args["contextHandle"]:
                    raise OperationError("expired_context", "Unknown engineering context.")
                self.context["invalid"] = True
            self.backend.close_context()
            with self.lock:
                self.context = None
            return dict(success=True, closed=True)
        if operation == "safety.grant":
            with self.lock:
                if "contextHandle" in args:
                    self.context_for(args["contextHandle"])
                for name in args["operations"]:
                    entry = self.catalog.get(name)
                    if not entry["requiresGrant"]:
                        raise OperationError(
                            "invalid_grant", "Grant only operations marked requiresGrant."
                        )
                    scope = (
                        "contextHandle"
                        if entry["engineering"]
                        else "configPath"
                        if name == "scope.start"
                        else "amsNetId"
                    )
                    if scope not in args:
                        raise OperationError("invalid_grant", f"{name} requires scope {scope}.")
                return dict(success=True, **self.grants.create(args))
        return None

    def run(self, operation, args, checkpoint, progress):
        checkpoint()
        result = self.control(operation, args)
        if result is not None:
            return result
        if operation == "workflow.sequence":
            self.preflight(operation, args)
            results = []
            for index, step in enumerate(args["steps"]):
                try:
                    checkpoint()
                    progress(f"Step {index + 1}/{len(args['steps'])}: {step['operation']}")
                    result = self.run(step["operation"], step["arguments"], checkpoint, progress)
                except OperationError as exc:
                    result = dict(success=False, code=exc.code, message=str(exc))
                except Exception as exc:
                    result = dict(
                        success=False,
                        outcomeUnknown=True,
                        code="unexpected_failure",
                        message=str(exc),
                    )
                results.append(dict(operation=step["operation"], result=result))
                if not result.get("success"):
                    return dict(
                        success=False,
                        outcomeUnknown=bool(result.get("outcomeUnknown")),
                        completedSteps=index,
                        failedStep=index,
                        steps=results,
                        code=result.get("code", "step_failed"),
                    )
            return dict(success=True, completedSteps=len(results), steps=results)
        if operation == "workflow.wait_state":
            return self.wait_state(args, checkpoint, progress)
        self.preflight(operation, args)
        entry = self.catalog.get(operation)
        with self.lock:
            context = (
                dict(self.context_for(args["contextHandle"])) if entry["engineering"] else None
            )
        try:
            checkpoint()
            progress("Dispatching " + operation)
            result = self.backend.run(entry, args, context)
            if not isinstance(result, dict):
                raise OperationError(
                    "invalid_backend_result", "Backend must return a structured object."
                )
            if result.get("outcomeUnknown") and context:
                with self.lock:
                    self.context["invalid"] = True
            if operation == "scope.start":
                if result.get("success"):
                    self.recording = secrets.token_urlsafe(32)
                    result["recordingHandle"] = self.recording
                else:
                    self.backend.scope.close()
            if operation == "scope.stop" and result.get("success"):
                self.recording = None
            progress("Completed " + operation)
            return result
        except Exception:
            if context:
                with self.lock:
                    self.context["invalid"] = True
            if operation == "scope.start":
                self.backend.scope.close(force=True)
            raise
        finally:
            if context:
                with self.lock:
                    self.context["started"] = self.backend.started()

    def wait_state(self, args, checkpoint, progress):
        deadline = time.monotonic() + args["timeoutSeconds"]
        attempts = 0
        last = {}
        while True:
            checkpoint()
            attempts += 1
            last = self.run(
                "runtime.get_state",
                dict(amsNetId=args["amsNetId"], port=args["port"]),
                checkpoint,
                progress,
            )
            if last.get("success") and last.get("adsState") == args["expectedState"]:
                return dict(success=True, attempts=attempts, observation=last)
            if time.monotonic() >= deadline:
                return dict(
                    success=False, code="state_timeout", attempts=attempts, observation=last
                )
            until = min(deadline, time.monotonic() + args["intervalSeconds"])
            while time.monotonic() < until:
                checkpoint()
                time.sleep(min(0.1, max(0, until - time.monotonic())))

    def close(self):
        self.jobs.close()
        if not self.jobs.worker.is_alive():
            self.backend.close()
