# Changelog

## Unreleased

### Added

- `hmi.refresh_mappings` refreshes ADS PLC symbol mappings, as the designer's
  *Refresh mapping* does. It checks targets over ADS first, preserves mapping
  settings and restores a mapping whose remap fails. The catalog now has 73
  entries (69 discoverable).

### Fixed

- When ports 3000 and 13000 are busy, the HMI worker writes other engineering
  ports into the user's `.hmiproj`. Once its shell has exited, the worker now
  restores each `.hmiproj` whose only change is those ports (measured on
  `context.close`).
- The engineering server also writes those ports into the `ENDPOINTS` of
  `TcHmiSrv.Config.default.json` when the project loads. If the ports of
  `http://127.0.0.1` elements are the only change in that array, the worker now
  puts back the original array and keeps every other change, a mapping refresh
  included (measured on `context.close`). Nothing is restored while an
  engineering server still holds its lock, or after the job is released.
- The worker starts an HMI shell in a Windows job and ends the job once the
  shell has exited. On `context.close`, no engineering server of the context
  outlived it in five runs. A process that breaks away from the job, or that
  survives a retry, is left running, and so is the job when the worker dies.

## 3.1.0 — 2026-11-07

### Added

- HMI-only contexts bind an exact project and HTTPS server, independently of a
  PLC. The existing owned XAE worker loads HMI-only solutions without requiring
  a `.tsproj`, choosing a PLC target or activating TwinCAT.
- `hmi.inspect`, `hmi.build`, `hmi.preflight`, `hmi.publish` and `hmi.status`
  use TE2000's native automation API. Publication preserves server files and
  extension data, requires an explicit context grant and matching preflight
  fingerprint, submits once and reports completion without screen automation.
- Source/configuration and publisher identity are checked before dispatch.
  Wrong targets, destructive profiles and changed inputs are refused. Passwords
  and raw vendor exception text are not exposed. Status remains readable after
  an uncertain result while the same worker is alive; no mutation is replayed.
- A repeatable stdio/native HMI qualification script and workflow guide.

### Upgrade

Update the repository, rebuild with `scripts/setup.ps1` and reconnect the MCP
client to load application 3.1.0. The three public tools, MCP 2026-07-28 and SDK
2.2.0 are unchanged; six catalog entries bring the total to 72 (68 discoverable).
TE2000 HMI Engineering is optional and needed only for HMI operations. Keep its
saved publishing credentials local. No additional PLC deployment license or
PLC restart is introduced by the HMI publisher.

### Validation and limits

- 104 Python contract/lifecycle/grant tests and 231 native policy checks passed,
  along with Ruff and dependency checks. They cover existing PLC behavior and
  HMI isolation/refusal.
- Real stdio/native qualification on TE2000 14.18.57.1: inspect, wrong-target
  refusal, HMI build, changed-fingerprint refusal, publish, duplicate receipt
  replay, status and closure. The local HIL PLC kept running at Max.
- The first release requires HTTPS and non-destructive, abort-on-error profiles.
  It refuses external/dynamic source inputs. Package cache contents are not
  fingerprinted. The native publisher can save generated metadata; the result
  reports changes observed during publication. It cannot roll back a blocking
  vendor call. Other SDK result shapes remain unqualified and fail closed.


## 3.0.1 — 2026-11-05

### Fixes

- `engineering.activate`, `workflow.deploy` and `workflow.test` now verify that
  the activation reached the target before continuing, instead of sleeping a
  fixed 5–10 s. On a local user-mode runtime, XAE 3.1.4026 reported a
  successful activation that never reached the runtime: it had already replaced
  the PLC boot files, but `Boot\CurrentConfig.xml` still held the previous
  project. The deploy then restarted TwinCAT anyway. The PLC reported a
  mismatched symbol description, and the user-mode system service could crash.
- For targets whose boot folder is on this PC, completion now requires a
  rewritten, complete and stable `CurrentConfig.xml`, plus a settled system
  service (ADS port 10000). A rewrite of only the PLC boot files does not count.
  If `CurrentConfig.xml` is not rewritten within 30 s, the same activation is
  dispatched once more, the documented manual recovery, and verified again.
- If the activation still does not land (final attempt timeout 120 s,
  `TWINCAT_MCP_ACTIVATION_TIMEOUT_SECONDS`), the operation fails, names the
  missing signal and does not restart TwinCAT. The error warns that the target
  may already hold the new PLC boot files.
- A landed activation is not visible over ADS. Remote targets therefore wait for
  a settled system service and are reported as unverified, with a warning. If
  nothing is readable, a fixed 20 s delay applies, also unverified.
- Results include an `activation` receipt (verified, signal, attempts, elapsed
  time, before/after state and boot configuration, observations). Deploy step 7
  records the verdict. See [activation completion](docs/activation.md).

### Upgrade

Stop the server, pull, run `scripts/setup.ps1` (rebuilds the native worker) and
restart the MCP connection. Verify application version `3.0.1`. Operation
schemas, the three public tools, MCP 2026-07-28 and Python SDK 2.2.0 are
unchanged. A local activation can take up to 30 s plus the final timeout before
it fails. Remote activations report `verified:false`; check the target's boot
configuration if the PLC reports a symbol mismatch.

### Validation

47 new scripted-probe activation cases and the existing 162 native checks
passed. They cover a PLC-only rewrite, a second attempt that lands, bounded
retries, partial and churning boot files, and remote, ReadState-only and
unreadable targets. 91 Python tests and Ruff passed.

Qualified live on a local UmRT (XAE/XAR 3.1.4026.26) with a StationHmi
solution, with a read-only ADS/boot-folder probe running alongside:

- **The first activation after loading the solution** did not reach the
  runtime in all four XAE sessions. In the two builds of this change without
  a second attempt, `CurrentConfig.xml` was not rewritten within 180 s, and the
  system service stayed in Run (restart index 60). XAE still replaced
  `Plc\Port_851.*`. The wait timed out and did not restart TwinCAT.
- **The second activation in the same session** rewrote `CurrentConfig.xml`
  during the call (about 4–6 s) in both sessions that dispatched one. The
  system service state and restart index did not change.
- **The released `deploy` (build, boot project, activate, restart).** Attempt 1
  was not rewritten within 30 s, and attempt 2 landed and was verified (33.4 s
  in total). The restart then took the runtime through Stop to Run (restart
  index 60→64). Ports 10000 and 851 were in Run, and `CurrentConfig.xml` named
  the new project.

Local XAR registry discovery and remote targets were not qualified on a bench.
`workflow.test` uses the same verification but was not run live.

## 3.0.0 — 2026-10-28

### Safety corrections and breaking contracts

- Library export now uses the documented non-installing API once, without
  speculative reflection, guessed flags or mutation retries. Preview performs
  no build/export. Output is staged, hashed and published to a new `.library`.
- `engineering.generate_library`, `runtime.record` and `scope.create_config`
  require scoped grants. Existing output files are never overwritten. Recording
  outputs must be absolute `.csv` paths; configuration outputs use `.tcscopex`.
- Failed ADS batch items now fail the overall receipt and stop workflows.
  Partial results, symbol-key casing and dispatched-write uncertainty survive.
  BOOL conversion rejects invalid values. Symbol data and recording rows/cells
  have aggregate bounds. Recording rejects unknown types and incomplete initial
  snapshots instead of inventing values.
- `scope.start`, `scope.stop`, `scope.status` and `scope.export` are disabled
  pending qualified native capture/export. Placeholder files and estimated
  sample counts are removed. Search exposes 62 available contracts; describe
  retains all 66 with explicit unavailable metadata.
- Dialog automation verifies process start time and retains identity through
  each callback. Janitor record deletion compares/deletes one locked file handle
  so concurrent replacement records survive.
- Patched reviewed dependency advisories in PyJWT, certifi, OpenTelemetry and
  packaging tools. Added read-only PR checks with SHA-pinned actions and explicit
  contributor safety/architecture review requirements.

### Upgrade

Finish active operations and stop the server before updating. Run
`scripts/setup.ps1`, then restart the MCP connection and open new contexts and
grants. Verify application version `3.0.0`. Update artifact-writer calls to
supply grants. Native CLI read/record variable lists use JSON arrays. Existing
artifacts require a new output filename. Scope capture/export cannot be enabled
by rebuilding with TE13xx alone.

The three public tools, MCP 2026-07-28 and Python SDK 2.2.0 remain unchanged.
See [the contributor review](docs/contributor-security-review.md) for findings,
retained capabilities, dependency evidence and qualification limits.

### Validation

91 Python tests and 162 native checks passed, plus Ruff, dependency compatibility
checks and offline stdio/XAE library-export qualification. Advisory queries for
66 resolved Python/NuGet versions had no matching reviewed advisories after
updates. No PLC writes, login, activation or download were performed.

## 2.3.0 — 2026-10-28

### Capabilities

- Added `engineering.move_object` for relocating a whole PLC object or project
  folder within the selected PLC. It preserves native file bytes, GUIDs and
  nested methods/properties. The catalog has 66 operations; the three public
  MCP tools remain unchanged.
- Moves require offline PLC projects, an existing destination, a current source
  subtree hash, a context-scoped grant and confirmation. Conflicts, category
  roots, cross-PLC paths, individual members and unsupported linked/conditional
  layouts are rejected.
- Relocation saves and closes the owned solution, moves files and updates
  project entries, then reopens XAE and verifies exact source/native bytes.
  Recovery manifests and project copies precede mutation. Guarded rollback and
  explicit uncertain receipts cover failures; no crash atomicity is claimed.

### Upgrade

Stop the MCP server, run `scripts/setup.ps1`, and restart it through the client.
Verify `system.status` reports application version `2.3.0` and 66 operations.
Use [the move guide](docs/moving-objects.md) for supported layouts, reload
behavior, deadlines and recovery. Moving is optional; existing create, update,
delete, rename and import/export operations remain available.

### Validation

- 83 Python protocol/orchestration/lifecycle tests and Ruff passed.
- Native MSBuild and 55 object-policy, 24 feedback/janitor and 40 relocation
  checks passed, including failures between file and project edits, reopen
  failures, external-edit preservation and recovery records.
- Real stdio MCP-to-XAE 3.1.4026.26 checks passed moves of an FB with nested
  members, a folder containing nine source files, empty folders and move-back.
  Native bytes remained identical and subsequent compiler/artifact checks
  passed. A forced deadline retained source bytes and recovery evidence while
  expiring the context. No PLC login, activation or download was performed.

## 2.2.0 — 2026-10-28

### Capabilities and fixes

- Added `workflow.build_verify` for explicit TMC symbol/type expectations and
  paged `engineering.symbols` inspection. Build receipts separate compiler
  success from artifact verification, include source/artifact hashes and
  identify files retained after compiler failure. The catalog has 65 operations;
  the three public MCP tools remain unchanged.
- Builds wait for their own DTE completion events, capture output after dispatch
  preparation and enforce a 1–600-second deadline (default 180). An expired
  deadline terminates the owned shell, records an uncertain outcome and expires
  the context. Clean reports retained TMC files without deleting them manually.
- `check_all_objects` honors the native compiler's Boolean result. Failed
  compilation cannot return zero errors merely because XAE labels source
  diagnostics as medium severity; inferred classifications retain raw severity.
- Object create/update accept an explicit `linkingPolicy` for POUs and GVLs.
  The default preserves source. Verification distinguishes unused-object
  elimination from missing expected generated content.
- Execute supports waits up to 120 seconds. `job.wait` waits on an existing
  receipt without resubmission; cancelling a waiter leaves the job running.
- Python shutdown responds to stdio EOF and client exit, including Windows
  virtual-environment launchers. Idle healthy clients remain connected.
- Fixed TcXaeShell 15.0 launch registration and COM ownership, prompt session
  publication, bounded close and host cache invalidation. A lost owned shell
  cannot silently become a replacement engineering session.
- Hardened native orphan cleanup with strict process fingerprints, pinned
  handles, atomic session publication and explicit unresolved-record reporting.
  Unverified processes are left untouched. State-directory isolation now includes
  native session records.

### Upgrade

Finish active operations, stop the MCP server, run `scripts/setup.ps1`, and
restart it through the client. Verify `system.status` reports application
version `2.2.0` and 65 operations. Open new contexts and grants after restart.
See [build feedback](docs/build-feedback.md) for artifact interpretation,
linking policy, deadlines and process-lifetime controls.

### Validation

- 81 Python protocol, orchestration and lifecycle tests passed, including real
  subprocess client-exit/EOF checks with inherited pipes.
- The native harness passed 55 object-policy cases and 24 build-output,
  artifact, linking-policy and janitor checks using test-owned child processes.
- Real stdio MCP-to-XAE qualification on TwinCAT 3.1.4026.26 passed immediate
  referenced/explicitly linked object builds, missing-symbol failures and type expectations,
  source-change detection, failed compiler diagnostics and retained-TMC evidence.
  A forced deadline returned an uncertain receipt and expired the context.
  No PLC login, activation or download was performed.
- A blanket post-edit compiler-cache failure was not reproduced in these
  application tests. Library-specific compiler behavior, PLC deployment, online
  change and optional TE13xx Scope require separate qualification.

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
