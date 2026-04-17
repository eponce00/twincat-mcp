"""
Safety / arming layer for destructive tools.

Dangerous tools (activate, restart, deploy, set_state, write_var) require
the server to be "armed" before they run. Arming auto-expires after
`ARMED_MODE_TTL` seconds.

The most destructive subset (activate, restart, deploy) additionally
require an explicit `confirm: "CONFIRM"` argument on each call.

Everything in this module is module-level state + pure functions; it does
not depend on the shell host or any tool handler. The behavior is
identical to what used to live at the top of server.py.
"""

import os
import time

from .defaults import is_local_target, resolve_ams_net_id

# -----------------------------------------------------------------------------
# Configuration
# -----------------------------------------------------------------------------

# Armed mode TTL in seconds (default: 5 minutes).
ARMED_MODE_TTL = int(os.environ.get("TWINCAT_ARMED_TTL", 300))

# Tools that require armed mode.
DANGEROUS_TOOLS = [
    "twincat_activate",
    "twincat_restart",
    "twincat_deploy",
    "twincat_set_state",
    "twincat_write_var",
]

# Tools that additionally require `confirm: "CONFIRM"` on every call.
CONFIRMATION_REQUIRED_TOOLS = [
    "twincat_activate",
    "twincat_restart",
    "twincat_deploy",
]

CONFIRM_TOKEN = "CONFIRM"

# Low-level (C# CLI) batch step commands that count as dangerous when used
# inside twincat_batch. If any step in a batch matches one of these, the
# batch as a whole is treated as dangerous and requires armed mode.
DANGEROUS_BATCH_COMMANDS = {
    "activate",
    "restart",
    "set-state",
    "write-var",
}

# Low-level batch step commands that also require an explicit
# confirm='CONFIRM' at the batch level (same policy as twincat_activate /
# twincat_restart).
CONFIRMATION_REQUIRED_BATCH_COMMANDS = {
    "activate",
    "restart",
}

# -----------------------------------------------------------------------------
# Mutable state
# -----------------------------------------------------------------------------

_armed_state = {
    "armed": False,
    "armed_at": None,
    "reason": None,
}


# -----------------------------------------------------------------------------
# Public API
# -----------------------------------------------------------------------------

def is_armed() -> bool:
    """True iff armed mode is currently active and not TTL-expired."""
    if not _armed_state["armed"]:
        return False

    if _armed_state["armed_at"] is None:
        return False

    elapsed = time.time() - _armed_state["armed_at"]
    if elapsed > ARMED_MODE_TTL:
        _armed_state["armed"] = False
        _armed_state["armed_at"] = None
        _armed_state["reason"] = None
        return False

    return True


def get_armed_time_remaining() -> int:
    """Seconds remaining in armed mode, or 0 if not armed."""
    if not is_armed():
        return 0

    elapsed = time.time() - _armed_state["armed_at"]
    return max(0, int(ARMED_MODE_TTL - elapsed))


def get_armed_reason() -> str | None:
    """Current arm reason, or None if not armed."""
    if not is_armed():
        return None
    return _armed_state["reason"]


def arm_dangerous_operations(reason: str) -> dict:
    """Arm dangerous operations with a reason. Returns a status payload."""
    _armed_state["armed"] = True
    _armed_state["armed_at"] = time.time()
    _armed_state["reason"] = reason
    return {
        "armed": True,
        "ttl_seconds": ARMED_MODE_TTL,
        "reason": reason,
    }


def disarm_dangerous_operations() -> dict:
    """Disarm dangerous operations immediately."""
    _armed_state["armed"] = False
    _armed_state["armed_at"] = None
    _armed_state["reason"] = None
    return {"armed": False}


def check_armed_for_tool(tool_name: str, arguments: dict | None = None) -> tuple[bool, str]:
    """
    Check if a tool is allowed to run. Returns (allowed, message).

    For tools in DANGEROUS_TOOLS, armed mode is required unconditionally.
    For twincat_run_tcunit, armed mode is required only when targeting a
    remote PLC — the effective target is resolved via `resolve_ams_net_id`,
    so if `TWINCAT_DEFAULT_AMS_NET_ID` points at a remote rig and the
    agent didn't pass one, arming is still required.
    """
    if tool_name not in DANGEROUS_TOOLS:
        # Special case: twincat_run_tcunit requires armed mode for remote
        # targets. NOTE: we check `arguments is not None` (not truthiness)
        # because an empty dict `{}` is falsy in Python — and the agent
        # legitimately may pass no args now that `amsNetId` defaults are
        # resolved server-side. Skipping the check in that case would let
        # a remote default silently bypass arming.
        if tool_name == "twincat_run_tcunit" and arguments is not None:
            effective = resolve_ams_net_id(arguments.get("amsNetId"))
            if not is_local_target(effective):
                if not is_armed():
                    return False, (
                        f"🔒 SAFETY: Running TcUnit tests on remote PLC '{effective}' requires armed mode.\n\n"
                        f"Local testing (127.0.0.1.1.1) does not require arming.\n"
                        f"To run tests on a remote PLC:\n"
                        f"1. Call 'twincat_arm_dangerous_operations' with a reason\n"
                        f"2. Then retry this operation within {ARMED_MODE_TTL} seconds\n\n"
                        f"This safety mechanism prevents accidental PLC modifications."
                    )
        return True, ""

    if not is_armed():
        return False, (
            f"🔒 SAFETY: '{tool_name}' is a dangerous operation that requires armed mode.\n\n"
            f"The server is currently in SAFE mode. To execute this operation:\n"
            f"1. Call 'twincat_arm_dangerous_operations' with a reason\n"
            f"2. Then retry this operation within {ARMED_MODE_TTL} seconds\n\n"
            f"This safety mechanism prevents accidental PLC modifications."
        )

    return True, f"⚠️ Armed mode active (reason: {_armed_state['reason']})"


def check_confirmation(tool_name: str, arguments: dict) -> tuple[bool, str]:
    """
    Check whether a confirm='CONFIRM' token has been provided for tools
    that require one. Returns (confirmed, message).
    """
    if tool_name not in CONFIRMATION_REQUIRED_TOOLS:
        return True, ""

    confirm = arguments.get("confirm", "")
    if confirm != CONFIRM_TOKEN:
        # Show the *effective* target, including the default fallback, so
        # the agent knows exactly what PLC it's about to hit if it proceeds.
        target = resolve_ams_net_id(arguments.get("amsNetId"))
        return False, (
            f"⚠️ CONFIRMATION REQUIRED for '{tool_name}'\n\n"
            f"This operation will affect: {target}\n\n"
            f"To proceed, add the parameter:\n"
            f"  confirm: \"{CONFIRM_TOKEN}\"\n\n"
            f"This ensures intentional execution of destructive operations."
        )

    return True, ""
