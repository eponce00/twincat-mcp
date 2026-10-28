# Changelog

## 2.1.0 — 2026-10-28

### Capabilities

- Added nine PLC object operations for browsing, source reads/updates, typed
  creation, nested folders, renaming, guarded deletion and native/PLCopen
  import/export. The three public MCP tools remain unchanged.
- Offline authoring uses Automation Interface and rejects logged-in projects.
  Mutations require context-scoped grants, and updates/deletion/renames require
  current object/subtree hashes. Recursive folder deletion has a separate gate.
- Native imports verify object GUIDs and file hashes; exports preserve saved
  native bytes or produce PLCopen XML without overwriting destination files.
- Added grant-scoped `system.route_upsert` and `system.route_remove` operations
  backed by Beckhoff's `TcXaeMgmt` PowerShell module.
- Secure route creation requires the target certificate's SHA-256 fingerprint
  and a DPAPI-protected `PSCredential` file. Plaintext password arguments are
  not accepted or logged.
- `system.routes` now reports Beckhoff route metadata, Secure ADS mode,
  certificate fingerprints and current availability.

### Upgrade

Finish active operations and stop the MCP server before updating. Run
`scripts/setup.ps1` to rebuild the native worker and install pinned dependencies,
then restart the MCP server. `system.status` reports version `2.1.0` and 62
operations. Existing v2 tool calls remain supported; object authoring uses the
new catalog operations described in [the object guide](docs/plc-objects.md).

### Validation

- A real stdio MCP-to-XAE check passed scoped-grant creation, object read/delete,
  duplicate-request receipt recovery and clean server shutdown.
- Offline XAE 3.1.4026.26 qualification passed source creation for all supported
  object kinds, nested folders and members, renaming, recursive deletion, native
  POU/interface/GVL/DUT round trips and a PLCopen hierarchy round trip. Fresh builds
  passed with zero errors or warnings and generated the expected new TMC symbols.
  No PLC login, activation or download was performed.
- The automated protocol and orchestration suite covers 66 cases, with a
  separate 55-case native in-memory object-policy harness.
- A dedicated Beckhoff bench passed local route removal, self-signed route
  recreation and an ADS system-port `Run` verification through the MCP engine.

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
