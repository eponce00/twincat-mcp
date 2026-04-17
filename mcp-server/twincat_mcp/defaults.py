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

This module centralises that. Set `TWINCAT_DEFAULT_AMS_NET_ID` in the
MCP server's environment (e.g. in `~/.cursor/mcp.json` → `env`) and
every tool that accepts `amsNetId` will fall back to it when the agent
doesn't supply one. If the env var is unset we keep the historic
`127.0.0.1.1.1` default so nothing breaks for existing users.

Usage
-----
- `DEFAULT_AMS_NET_ID` — the resolved default string, computed once at
  import time.
- `resolve_ams_net_id(value)` — helper for handlers: returns the
  caller-provided value when truthy, otherwise `DEFAULT_AMS_NET_ID`.
- `is_local_target(ams_net_id)` — True when the given address targets
  the local runtime (`127.0.0.1.*`). Used by the armed-mode gate so
  running e.g. TcUnit against a remote PLC still requires arming even
  when the default target is remote.
- `describe_default_for_schema()` — a human-readable suffix for tool
  schema descriptions so the agent sees the effective default in
  `list_tools` output.
"""

from __future__ import annotations

import os
import sys

# Historic fallback. Used when no env var is set, so existing users who
# always run against the local PLC see zero behaviour change.
_FALLBACK_AMS_NET_ID = "127.0.0.1.1.1"

_ENV_VAR = "TWINCAT_DEFAULT_AMS_NET_ID"


def _resolve_default_from_env() -> str:
    """Read the env var once; fall back to the historic localhost default."""
    raw = os.environ.get(_ENV_VAR, "").strip()
    return raw or _FALLBACK_AMS_NET_ID


DEFAULT_AMS_NET_ID: str = _resolve_default_from_env()


def resolve_ams_net_id(value: str | None) -> str:
    """
    Return `value` if the agent supplied a non-empty AMS Net ID,
    otherwise return the configured default.

    Centralising this means every amsNetId-accepting handler has
    identical fall-back semantics — empty string, None, and whitespace
    all collapse to the default.
    """
    if value is None:
        return DEFAULT_AMS_NET_ID
    stripped = value.strip()
    return stripped or DEFAULT_AMS_NET_ID


def is_local_target(ams_net_id: str | None) -> bool:
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
    if DEFAULT_AMS_NET_ID == _FALLBACK_AMS_NET_ID:
        return (
            f"Optional. Defaults to {DEFAULT_AMS_NET_ID} (local runtime). "
            f"Set the `{_ENV_VAR}` env var in your MCP client config to "
            f"change the default (e.g. point it at your test rig)."
        )
    return (
        f"Optional. Defaults to {DEFAULT_AMS_NET_ID} "
        f"(configured via `{_ENV_VAR}` in your MCP client config). "
        f"Pass an explicit value here to override for a single call."
    )


# Log the effective default on import so the operator sees it once at
# server start. stderr is the MCP-safe channel (stdout is the JSON-RPC
# stream).
if DEFAULT_AMS_NET_ID != _FALLBACK_AMS_NET_ID:
    print(
        f"[twincat-mcp] Default target PLC: {DEFAULT_AMS_NET_ID} "
        f"(from {_ENV_VAR})",
        file=sys.stderr,
    )
else:
    print(
        f"[twincat-mcp] Default target PLC: {DEFAULT_AMS_NET_ID} "
        f"(fallback; set {_ENV_VAR} to override)",
        file=sys.stderr,
    )
