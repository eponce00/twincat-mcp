"""
Unified step dispatch — the single entry point tool handlers use to run a
StepDispatcher command against the TwinCAT automation interface.

`run_shell_step` prefers the persistent shell host (one DTE for the MCP
server's lifetime). If the host is unavailable — disabled, failed to
start, crashed mid-call, exe missing, etc. — it transparently falls back
to a one-shot CLI invocation via `TcAutomation.exe batch`.

The CLI fallback wraps the single command as a one-step batch, so the C#
side only has to support the batch flow — no per-command argparse
scaffolding is required in the wrapper.
"""

import json
import os
import sys
import tempfile

from .cli import run_tc_automation_with_progress
from .host import (
    HostError,
    _ci_wrap,
    drop_shell_host,
    get_shell_host,
)


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

    Returns (result_dict, progress_messages). The result dict is wrapped
    in a _CIDict so existing tool handlers can read PascalCase OR
    camelCase keys without change.
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
            # If the process died, drop the stale instance so the next
            # call gets a fresh start attempt.
            if not host.is_alive():
                drop_shell_host()

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
        inner = first.get("result") if isinstance(first.get("result"), dict) else None

        if first.get("success"):
            return _ci_wrap(inner or {}), progress

        # Failure. Prefer the step's own result payload — it carries the
        # rich structured diagnostics the handlers know how to format
        # (BuildResult.errors/warnings, CheckAllObjectsResult.errors,
        # StaticAnalysisResult.errors, etc). C#'s BatchCommand writes
        # `stepResult.error` as a short string derived from the payload,
        # and `batch_result.errorMessage` falls back to the literal
        # "Batch failed" when that short string is empty. Both are
        # strictly less informative than the payload itself, so we only
        # use them to *stamp* missing fields on the payload.
        if inner is not None:
            enriched = dict(inner)
            if enriched.get("success") is not False and enriched.get("Success") is not False:
                enriched["success"] = False
            err_msg = (
                enriched.get("errorMessage")
                or enriched.get("ErrorMessage")
                or enriched.get("error")
                or first.get("error")
                or batch_result.get("errorMessage")
                or "Step failed"
            )
            enriched.setdefault("errorMessage", err_msg)
            enriched.setdefault("error", err_msg)
            return _ci_wrap(enriched), progress

        # No payload at all — synthesize a minimal error-shaped dict.
        err_msg = first.get("error") or batch_result.get("errorMessage") or "Step failed"
        return _ci_wrap({
            "success": False,
            "errorMessage": err_msg,
            "error": err_msg,
        }), progress

    # Batch itself failed before the step ran.
    err_msg = batch_result.get("errorMessage") or "Batch dispatch failed"
    return _ci_wrap({
        "success": False,
        "errorMessage": err_msg,
        "error": err_msg,
    }), progress
