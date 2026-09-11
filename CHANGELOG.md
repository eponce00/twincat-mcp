# Changelog

## 2.0.0 — 2026-09-11

TwinCAT MCP exposes a fixed discovery interface backed by 51 operations.
The application targets MCP 2026-07-28 with the official Python SDK 2.2.0.

### Breaking changes

- Clients use `twincat_search`, `twincat_describe` and `twincat_execute`.
  Per-operation MCP tool names are no longer supported.
- Engineering operations require an explicit context handle binding the solution,
  target and PLC. Runtime operations require an explicit AMS Net ID.
- Global arming and persistent default-target settings are removed. Mutations
  marked `requiresGrant` require a scoped grant; high-impact operations also
  require the confirmation field specified in their schemas.
- Submitted operations require a request key and return durable job receipts.
  Results are structured objects; large results are retrieved in pages.
- Python dependencies must be installed in the repository's `.venv`.
  The Release build output is `TcAutomation/bin/Release-v2`.

### Capabilities

- Bounded catalog search, selected schema descriptions and validated examples.
- Deployment, TcUnit, sequential execution and runtime-state polling workflows.
- Serialized native dispatch, duplicate-submission protection and persisted
  receipts with explicit uncertain outcomes.
- Job cancellation and immediate grant revocation between workflow steps.
- Explicit engineering and recording handles, fixed grant expiration, and
  source-hash and online-change verification requirements.

### Upgrade

1. Finish active operations and stop the MCP server through its client.
2. Run `scripts/setup.ps1` to build the worker and install pinned dependencies.
3. Configure the client to use the absolute `.venv/Scripts/python.exe` and
   `mcp-server/server.py` paths. Run `scripts/install-mcp.ps1` for VS Code.
4. Update stored prompts and integrations to use the three public tools.
   Search and describe operations to obtain their current argument contracts.
5. Reload the server and verify `system.status` reports application version
   `2.0.0`. Open new contexts and create grants as required.

### Validation

The automated suite covers 50 protocol and orchestration cases. The native
worker has passed build and start/status/shutdown checks without loading a solution.
Live PLC deployment, online change and optional TE13xx Scope qualification are
separate integration checks.

See [release procedure](docs/releasing.md).
