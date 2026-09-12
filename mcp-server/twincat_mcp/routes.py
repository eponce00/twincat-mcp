"""Bounded adapter for Beckhoff's TcXaeMgmt ADS-route cmdlets."""

import json
import os
import shutil
import subprocess
import tempfile
from pathlib import Path

_SCRIPT = Path(__file__).with_name("ads_routes.ps1")


def _powershell_executable() -> str:
    configured = os.environ.get("TWINCAT_POWERSHELL_EXE")
    if configured:
        path = Path(configured).resolve()
        if not path.is_file():
            raise FileNotFoundError(f"Configured TWINCAT_POWERSHELL_EXE does not exist: {path}")
        return str(path)

    executable = shutil.which("powershell.exe") or shutil.which("powershell")
    if not executable:
        raise FileNotFoundError("Windows PowerShell is required for TcXaeMgmt route operations.")
    return executable


def run_route_action(action: str, arguments: dict, timeout_seconds: int = 30) -> dict:
    """Run one fixed route action and return its JSON receipt.

    The payload travels through a temporary JSON file, so user-controlled route
    names and paths never become PowerShell source code. The credential file is
    imported inside Windows PowerShell and its secret is never returned.
    """

    if action not in {"Get", "Upsert", "Remove"}:
        raise ValueError(f"Unsupported route action: {action}")
    if not _SCRIPT.is_file():
        raise FileNotFoundError(f"ADS route script is missing: {_SCRIPT}")

    credential_path = arguments.get("credentialPath")
    needs_credential = action == "Upsert" or (
        action == "Remove" and arguments.get("removeRemote", False)
    )
    if needs_credential:
        candidate = Path(credential_path or "")
        if not candidate.is_absolute() or not candidate.is_file():
            return {
                "success": False,
                "errorMessage": (
                    "credentialPath must name an existing absolute DPAPI-protected "
                    "PSCredential file."
                ),
            }

    with tempfile.TemporaryDirectory(prefix="twincat-route-") as folder:
        payload_path = Path(folder) / "payload.json"
        payload_path.write_text(json.dumps(arguments), encoding="utf-8")

        command = [
            _powershell_executable(),
            "-NoLogo",
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            str(_SCRIPT),
            "-Action",
            action,
            "-PayloadPath",
            str(payload_path),
        ]

        try:
            completed = subprocess.run(
                command,
                capture_output=True,
                text=True,
                encoding="utf-8",
                timeout=timeout_seconds,
                check=False,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
        except subprocess.TimeoutExpired:
            return {
                "success": False,
                "outcomeUnknown": action != "Get",
                "errorMessage": "TcXaeMgmt route operation timed out; no replay occurred.",
            }

    stdout = completed.stdout.strip()
    try:
        result = json.loads(stdout)
        if not isinstance(result, dict):
            raise ValueError("Expected a JSON object")
    except (TypeError, ValueError):
        return {
            "success": False,
            "outcomeUnknown": action != "Get",
            "errorMessage": "TcXaeMgmt returned no valid JSON receipt.",
            "stderr": completed.stderr[-4000:],
        }

    if completed.returncode and result.get("success", True):
        result.update(
            success=False,
            outcomeUnknown=action != "Get",
            errorMessage=f"TcXaeMgmt exited with code {completed.returncode}.",
        )
    return result
