"""
Default target-PLC configuration for tools that accept an `amsNetId`.

Background
----------
Most of the agent-facing tools take an AMS Net ID so the agent can pick
which PLC to talk to. Historically each tool baked its own fallback
("127.0.0.1.1.1") which meant:

- agents had to pass `amsNetId` explicitly every time to target a
  specific machine, and
- there was no single place to configure "this MCP install always
  targets this PLC".

This module centralises that. Three sources can configure the default,
resolved in this order (highest precedence first):

  1) **Persistent config file** (`%LOCALAPPDATA%\\twincat-mcp\\config.json`).
     Written by the agent via the `twincat_set_default_target` MCP tool,
     so the agent can swap test rigs mid-conversation and have the new
     default survive into future conversations / server restarts.

  2) **`TWINCAT_DEFAULT_AMS_NET_ID` env var**, set in the MCP client's
     server config (e.g. `~/.cursor/mcp.json` → `env`). Good for "this
     machine always targets this PLC by default" — never changes unless
     the user edits their client config.

  3) **Hardcoded fallback** `127.0.0.1.1.1` (local runtime). Keeps
     long-standing installs working with zero configuration.

Public API
----------
- `DEFAULT_AMS_NET_ID` — the currently-effective default string,
  resolved at import time and updated in-place when
  `set_persistent_default` / `clear_persistent_default` are called.
- `resolve_ams_net_id(value)` — returns `value` if the caller supplied
  a non-empty AMS Net ID, else the current `DEFAULT_AMS_NET_ID`.
- `is_local_target(ams_net_id)` — True when the address targets the
  local runtime (`127.0.0.1.*`). Used by the armed-mode gate.
- `describe_default_for_schema()` — short description suffix injected
  into tool schema descriptions so agents see the effective default in
  `list_tools` output.
- `set_persistent_default(ams_net_id, reason=None)` — validate, write
  the config file atomically, update `DEFAULT_AMS_NET_ID` in-memory.
- `clear_persistent_default()` — remove the persisted value; the
  module re-resolves from env / hardcoded.
- `get_default_status()` — structured summary of what each source
  says and which one is active. Used by the handler for human output.
"""

from __future__ import annotations

import json
import os
import re
import sys
import tempfile
import time
from typing import Optional

# Historic fallback. Used when nothing else is set, so existing users who
# always run against the local PLC see zero behaviour change.
_FALLBACK_AMS_NET_ID = "127.0.0.1.1.1"

_ENV_VAR = "TWINCAT_DEFAULT_AMS_NET_ID"

# AMS Net ID format: six dot-separated 0-255 octets. Used to validate
# input before we write it to disk — no point persisting garbage.
_AMS_NET_ID_RE = re.compile(
    r"^(?:\d{1,3}\.){5}\d{1,3}$"
)


# ---------------------------------------------------------------------------
# Config-file location
# ---------------------------------------------------------------------------

def _config_dir() -> str:
    """
    Return the directory for persistent MCP state. Matches where the C#
    host writes session files (`%LOCALAPPDATA%\\twincat-mcp\\`), so
    everything MCP-related lives in one place.

    Falls back to `~/.twincat-mcp/` on non-Windows or when LOCALAPPDATA
    isn't set (covers WSL, tests, forks).
    """
    base = os.environ.get("LOCALAPPDATA")
    if not base:
        base = os.path.join(os.path.expanduser("~"), "AppData", "Local")
        if not os.path.isdir(base):
            return os.path.join(os.path.expanduser("~"), ".twincat-mcp")
    return os.path.join(base, "twincat-mcp")


_CONFIG_FILE = os.path.join(_config_dir(), "config.json")
_CONFIG_KEY = "defaultAmsNetId"


# ---------------------------------------------------------------------------
# Validation + IO helpers
# ---------------------------------------------------------------------------

def _is_valid_ams_net_id(value: str) -> bool:
    """Basic syntactic check. Enforces six dotted octets in 0-255."""
    if not _AMS_NET_ID_RE.match(value):
        return False
    return all(0 <= int(octet) <= 255 for octet in value.split("."))


def _read_config() -> dict:
    """Load the config JSON, returning {} if missing or corrupt."""
    try:
        with open(_CONFIG_FILE, "r", encoding="utf-8") as fh:
            data = json.load(fh)
            return data if isinstance(data, dict) else {}
    except (FileNotFoundError, json.JSONDecodeError, OSError):
        return {}


def _write_config(data: dict) -> None:
    """
    Atomically rewrite the config file: write to a temp file in the same
    directory, fsync, then os.replace — so a crash mid-write can never
    leave the file half-populated.
    """
    os.makedirs(_config_dir(), exist_ok=True)
    fd, tmp_path = tempfile.mkstemp(
        prefix=".config.", suffix=".json.tmp", dir=_config_dir()
    )
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as fh:
            json.dump(data, fh, indent=2)
            fh.flush()
            os.fsync(fh.fileno())
        os.replace(tmp_path, _CONFIG_FILE)
    except Exception:
        try:
            os.remove(tmp_path)
        except OSError:
            pass
        raise


def _read_config_default() -> Optional[str]:
    """Return the persisted AMS Net ID if set, validated; else None."""
    data = _read_config()
    value = data.get(_CONFIG_KEY)
    if isinstance(value, str) and _is_valid_ams_net_id(value):
        return value
    return None


def _read_env_default() -> Optional[str]:
    raw = os.environ.get(_ENV_VAR, "").strip()
    if raw and _is_valid_ams_net_id(raw):
        return raw
    return None


# ---------------------------------------------------------------------------
# Resolution
# ---------------------------------------------------------------------------

def _resolve_effective_default() -> tuple[str, str]:
    """
    Walk the precedence chain: config file > env var > hardcoded.
    Returns (value, source) where `source` ∈ {"config", "env", "fallback"}.
    """
    persisted = _read_config_default()
    if persisted:
        return persisted, "config"
    env = _read_env_default()
    if env:
        return env, "env"
    return _FALLBACK_AMS_NET_ID, "fallback"


# Resolved once at import. `set_persistent_default` + `clear_persistent_default`
# mutate this at runtime so handlers see the new value within the current
# server process; a fresh server start re-resolves from disk.
DEFAULT_AMS_NET_ID, _DEFAULT_SOURCE = _resolve_effective_default()


# ---------------------------------------------------------------------------
# Public API used by handlers / safety gate
# ---------------------------------------------------------------------------

def resolve_ams_net_id(value: Optional[str]) -> str:
    """
    Return `value` if the agent supplied a non-empty AMS Net ID,
    otherwise return the current default.

    Centralising this means every amsNetId-accepting handler has
    identical fall-back semantics — empty string, None, and whitespace
    all collapse to the default.
    """
    if value is None:
        return DEFAULT_AMS_NET_ID
    stripped = value.strip()
    return stripped or DEFAULT_AMS_NET_ID


def is_local_target(ams_net_id: Optional[str]) -> bool:
    """
    True when `ams_net_id` targets the local runtime. The check matches
    any `127.0.0.1.*` form — TwinCAT uses `127.0.0.1.1.1` but we stay
    tolerant of alternate port suffixes agents might try.

    `None` / empty is treated as local, because the fallback default is
    local and we don't want the safety gate to fire before
    `resolve_ams_net_id` has run.
    """
    if not ams_net_id:
        return True
    return ams_net_id.strip().startswith("127.0.0.1")


def describe_default_for_schema() -> str:
    """
    A short sentence suitable for appending to an `amsNetId` schema
    description. Bakes the effective default into the tool schema so
    agents discover it through `list_tools` without an extra round-trip.
    """
    if _DEFAULT_SOURCE == "config":
        return (
            f"Optional. Defaults to {DEFAULT_AMS_NET_ID} "
            f"(persisted via `twincat_set_default_target`). "
            f"Pass an explicit value to override for a single call, "
            f"or call `twincat_set_default_target` to change the persistent default."
        )
    if _DEFAULT_SOURCE == "env":
        return (
            f"Optional. Defaults to {DEFAULT_AMS_NET_ID} "
            f"(configured via `{_ENV_VAR}` in your MCP client config). "
            f"Pass an explicit value to override for a single call."
        )
    return (
        f"Optional. Defaults to {DEFAULT_AMS_NET_ID} (local runtime). "
        f"Call `twincat_set_default_target` to change the persistent "
        f"default, or set `{_ENV_VAR}` in your MCP client config."
    )


# ---------------------------------------------------------------------------
# Persistent mutators — exposed to the agent via twincat_set_default_target
# ---------------------------------------------------------------------------

def set_persistent_default(ams_net_id: str, reason: Optional[str] = None) -> dict:
    """
    Persist a new default AMS Net ID and update the in-memory value so
    subsequent tool calls in THIS server process pick it up immediately.
    A fresh server start will read the same value from the config file.

    Raises ValueError on malformed input. Returns a dict summarising the
    transition for the handler to format.
    """
    global DEFAULT_AMS_NET_ID, _DEFAULT_SOURCE

    cleaned = (ams_net_id or "").strip()
    if not _is_valid_ams_net_id(cleaned):
        raise ValueError(
            f"'{ams_net_id}' is not a valid AMS Net ID. "
            f"Expected six dot-separated octets in 0-255, e.g. '5.22.157.86.1.1'."
        )

    previous_value = DEFAULT_AMS_NET_ID
    previous_source = _DEFAULT_SOURCE

    data = _read_config()
    data[_CONFIG_KEY] = cleaned
    data["setAt"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    if reason:
        data["setReason"] = reason
    elif "setReason" in data:
        # Drop a stale reason from a prior call so the file reflects
        # only the most recent caller's intent.
        del data["setReason"]
    _write_config(data)

    # Mutate the module-level globals so handlers using `DEFAULT_AMS_NET_ID`
    # (including the schema-description helper, for any client that re-reads
    # `list_tools`) see the new value without a server restart.
    DEFAULT_AMS_NET_ID = cleaned
    _DEFAULT_SOURCE = "config"

    return {
        "previousValue": previous_value,
        "previousSource": previous_source,
        "newValue": cleaned,
        "newSource": "config",
        "configFile": _CONFIG_FILE,
        "reason": reason,
    }


def clear_persistent_default() -> dict:
    """
    Remove the persisted default AMS Net ID. The module re-resolves
    from env var / hardcoded fallback and updates the in-memory value.
    Returns a dict describing the post-clear state.
    """
    global DEFAULT_AMS_NET_ID, _DEFAULT_SOURCE

    previous_value = DEFAULT_AMS_NET_ID
    previous_source = _DEFAULT_SOURCE

    data = _read_config()
    had_value = _CONFIG_KEY in data
    if had_value:
        del data[_CONFIG_KEY]
        data.pop("setAt", None)
        data.pop("setReason", None)
        if data:
            _write_config(data)
        else:
            # Empty config is the same as no config — don't leave an
            # empty file around.
            try:
                os.remove(_CONFIG_FILE)
            except OSError:
                _write_config(data)

    new_value, new_source = _resolve_effective_default()
    DEFAULT_AMS_NET_ID = new_value
    _DEFAULT_SOURCE = new_source

    return {
        "previousValue": previous_value,
        "previousSource": previous_source,
        "newValue": new_value,
        "newSource": new_source,
        "configFile": _CONFIG_FILE,
        "hadPersistedValue": had_value,
    }


def get_default_status() -> dict:
    """
    Structured snapshot of what each configuration source currently
    says. Useful for the `twincat_set_default_target` handler to render
    a human-readable summary, and for debugging.
    """
    return {
        "effective": DEFAULT_AMS_NET_ID,
        "source": _DEFAULT_SOURCE,
        "configFile": _CONFIG_FILE,
        "configValue": _read_config_default(),
        "envVar": _ENV_VAR,
        "envValue": _read_env_default(),
        "fallback": _FALLBACK_AMS_NET_ID,
    }


# ---------------------------------------------------------------------------
# Startup log — once per process, stderr only (stdout is the JSON-RPC stream)
# ---------------------------------------------------------------------------

if _DEFAULT_SOURCE == "config":
    print(
        f"[twincat-mcp] Default target PLC: {DEFAULT_AMS_NET_ID} "
        f"(persisted in {_CONFIG_FILE})",
        file=sys.stderr,
    )
elif _DEFAULT_SOURCE == "env":
    print(
        f"[twincat-mcp] Default target PLC: {DEFAULT_AMS_NET_ID} "
        f"(from {_ENV_VAR})",
        file=sys.stderr,
    )
else:
    print(
        f"[twincat-mcp] Default target PLC: {DEFAULT_AMS_NET_ID} "
        f"(fallback; set {_ENV_VAR} or call twincat_set_default_target to change)",
        file=sys.stderr,
    )
