"""Explicit grants with immutable scope and fixed monotonic expiration."""

import os
import secrets
import threading
import time

from .errors import OperationError


def scope_for(arguments):
    if "contextHandle" in arguments:
        return ("context", arguments["contextHandle"])
    if "configPath" in arguments:
        return ("scope", os.path.normcase(os.path.abspath(arguments["configPath"])))
    if "amsNetId" in arguments:
        return ("target", arguments["amsNetId"])
    raise OperationError("missing_scope", "Supply contextHandle, amsNetId, or configPath.")


class Grants:
    def __init__(self, clock=time.monotonic):
        self.clock = clock
        self.items = {}
        self.lock = threading.Lock()

    def create(self, arguments):
        if sum(k in arguments for k in ("contextHandle", "configPath", "amsNetId")) != 1:
            raise OperationError("invalid_scope", "A grant must have exactly one scope.")
        scope = scope_for(arguments)
        ttl = arguments.get("ttlSeconds", 900)
        with self.lock:
            self.items = {k: v for k, v in self.items.items() if v["expires"] > self.clock()}
            if len(self.items) >= 128:
                raise OperationError("grant_limit", "Revoke unused grants first.")
            handle = secrets.token_urlsafe(32)
            self.items[handle] = dict(
                scope=scope,
                operations=frozenset(arguments["operations"]),
                expires=self.clock() + ttl,
            )
        return dict(
            grantHandle=handle,
            expiresInSeconds=ttl,
            operations=arguments["operations"],
            scope=scope,
        )

    def check(self, operation, arguments):
        with self.lock:
            grant = self.items.get(arguments.get("grantHandle"))
            if not grant or grant["expires"] <= self.clock():
                raise OperationError("grant_required", "Supply an unexpired safety.grant handle.")
            if operation not in grant["operations"] or scope_for(arguments) != grant["scope"]:
                raise OperationError(
                    "grant_scope_mismatch",
                    "Grant does not authorize this operation and target/context.",
                )

    def revoke(self, handle):
        with self.lock:
            self.items.pop(handle, None)
        return {"revoked": True}
