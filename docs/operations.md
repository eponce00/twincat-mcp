# Operation catalog

The three public MCP tools expose the following 65 operations. Use
`twincat_describe` for current full input schemas, defaults, limits, prerequisites
and examples. This index summarizes the catalog; it does not replace validation.

Submitted operations return jobs and require a request key. Immediate operations
return directly without a request key. `job.wait` waits on an existing receipt
without a request key. Each marked operation requires a scoped grant.

[PLC object and folder guide](plc-objects.md) covers source authoring and import/export.
[Build feedback](build-feedback.md) covers compilation, TMC evidence and waits.

## Context

| Operation | Purpose | Required arguments | Grant | Execution |
| --- | --- | --- | --- | --- |
| `context.close` | Release this context and its owned shell when prior queued work finishes. Later calls using its handle fail. | `contextHandle` | No | Job |
| `context.open` | Reserve an exclusive engineering context for a solution, PLC and target. Shell opens lazily. Close when finished. | `solutionPath`, `amsNetId`, `plcName` | No | Job |
| `context.status` | Inspect a context without starting TwinCAT. | `contextHandle` | No | Immediate |

## Engineering

| Operation | Purpose | Required arguments | Grant | Execution |
| --- | --- | --- | --- | --- |
| `engineering.activate` | Download and activate the configuration on the context target. Requires an operation-scoped grant and CONFIRM. | `contextHandle`, `grantHandle`, `confirm` | Yes | Job |
| `engineering.build` | Build with build-scoped output and bounded completion. Return per-PLC TMC/source fingerprints and artifact provenance. Optional expectedSymbols/expectedTypes fail verification when missing. Unreferenced objects may be stripped. | `contextHandle` | No | Job |
| `engineering.check_all_objects` | Syntax-check all PLC objects, including unused objects, and honor the native compiler result. Does not generate code or prove runtime symbol inclusion. | `contextHandle` | No | Job |
| `engineering.clean` | Run the IDE clean operation, invalidating retained compile information for online change. Report retained TMC paths; clean does not guarantee all generated files are removed. | `contextHandle` | No | Job |
| `engineering.configure_rt` | Configure isolated real-time CPU count and load limit in the solution. | `contextHandle`, `grantHandle` | Yes | Job |
| `engineering.configure_task` | Enable or disable a named real-time task or configure its autostart setting. | `taskName`, `contextHandle`, `grantHandle` | Yes | Job |
| `engineering.create_folder` | Create and save one PLC project folder through Automation Interface. Use its returned path as parentPath for objects or nested folders. Offline only; no overwrite. | `parentPath`, `name`, `contextHandle`, `grantHandle` | Yes | Job |
| `engineering.create_object` | Create and save a PLC object through Automation Interface, then read back its source. Offline only. No compile, runtime download or overwrite. | `parentPath`, `name`, `objectType`, `contextHandle`, `grantHandle` | Yes | Job |
| `engineering.delete_object` | Delete a PLC source object or folder offline. Requires current object/subtree hash and confirmation. A nonempty folder requires recursive=true and recursiveConfirm=DELETE_CONTENTS. No rollback. | `path`, `expectedSha256`, `confirm`, `contextHandle`, `grantHandle` | Yes | Job |
| `engineering.disable_io` | Enable or disable top-level I/O devices in the solution configuration. | `contextHandle`, `grantHandle` | Yes | Job |
| `engineering.edit_plc_source` | Edit and save one POU section only if expectedSha256 matches. Requires the existing logged-in engineering context. Does not compile or apply the edit. | `path`, `section`, `text`, `expectedSha256`, `contextHandle`, `grantHandle` | Yes | Job |
| `engineering.export_objects` | Export selected PLC objects to a new absolute file. Native format exports one saved POU, DUT, GVL or interface including its nested members; PLCopen supports up to 32 paths. No destination overwrite. Requires a grant for filesystem writes. | `format`, `filePath`, `paths`, `contextHandle`, `grantHandle` | Yes | Job |
| `engineering.generate_library` | Build and export the context PLC as a .library artifact. Existing outputs are backed up; dryRun previews the export. | `contextHandle` | No | Job |
| `engineering.get_error_list` | Read the XAE Error List with severity and substring filters; waitSeconds permits a bounded delay for async messages. | `contextHandle` | No | Job |
| `engineering.import_objects` | Import a SHA256-verified native TwinCAT object or PLCopen XML file offline through Automation Interface. Rejects name collisions; never replaces or silently renames. Max 4 MiB. Native import requires parentPath; PLCopen preserves exported folders at the root. | `format`, `filePath`, `expectedFileSha256`, `contextHandle`, `grantHandle` | Yes | Job |
| `engineering.info` | Read solution, toolchain and PLC project information in the engineering context. | `contextHandle` | No | Job |
| `engineering.list_objects` | Browse one PLC project folder or object. Omit path to discover the selected PLC root. Returns bounded child metadata and nextOffset. | `contextHandle` | No | Job |
| `engineering.list_plcs` | List PLC projects, configured ADS ports and boot-project settings in the solution. | `contextHandle` | No | Job |
| `engineering.list_tasks` | List configured real-time tasks with cycle times, priorities and enabled state. | `contextHandle` | No | Job |
| `engineering.matching_login` | Log into the unchanged baseline in this engineering context. Requires retained compile information, matching configuration/platform, configured ADS port and a RUN application. Rejects download/change prompts. | `port`, `configuration`, `platform`, `confirm`, `contextHandle`, `grantHandle` | Yes | Job |
| `engineering.online_change` | Apply one online change in the already logged-in matching PLC context. Verify one counter increment and advancing cycles. Never substitute download, activate, restart or boot-project update. | `port`, `contextFile`, `cycleSymbol`, `expectedOnlineChangeCount`, `confirm`, `contextHandle`, `grantHandle` | Yes | Job |
| `engineering.read_object` | Read PLC object metadata and supported source sections. sha256 covers its path, source and descendants; use it for update/delete/rename. | `path`, `contextHandle` | No | Job |
| `engineering.read_plc_source` | Read a POU declaration or implementation and its exact UTF-8 SHA256 from the engineering context. | `path`, `section`, `contextHandle` | No | Job |
| `engineering.rename_object` | Rename one object or folder offline using XAE. Requires current object/subtree hash. References in other source are not automatically rewritten; inspect and compile afterward. | `path`, `expectedSha256`, `name`, `contextHandle`, `grantHandle` | Yes | Job |
| `engineering.restart` | Restart the TwinCAT runtime on the context target. Requires an operation-scoped grant and CONFIRM. | `contextHandle`, `grantHandle`, `confirm` | Yes | Job |
| `engineering.set_boot_project` | Configure autostart and generate the selected PLC boot project on the context target. | `contextHandle`, `grantHandle` | Yes | Job |
| `engineering.set_target` | Set the solution target to the AMS Net ID fixed by context.open. Does not activate. | `contextHandle`, `grantHandle` | Yes | Job |
| `engineering.set_variant` | Get or set the solution variant. Requires TwinCAT XAE 4024 or newer; omit variantName to query. | `contextHandle`, `grantHandle` | Yes | Job |
| `engineering.static_analysis` | Run TE1200 static analysis and return rule violations. Requires the appropriate license. | `contextHandle` | No | Job |
| `engineering.symbols` | Read a bounded page of runtime symbols or data types from the selected PLC TMC. Returns file/source fingerprints and provenance of the last build observed in this context. Existing/stale files are not proof of a successful current build. | `contextHandle` | No | Job |
| `engineering.update_object` | Edit and save declaration, ST implementation or explicit linking policy offline. Requires the full object sha256 from read_object. Logged-in projects are rejected; online source editing remains separate. | `path`, `expectedSha256`, `contextHandle`, `grantHandle` | Yes | Job |

## Job

| Operation | Purpose | Required arguments | Grant | Execution |
| --- | --- | --- | --- | --- |
| `job.cancel` | Cancel queued work or stop before the next workflow step. Dispatched commands may finish; poll receipt. | `jobHandle` | No | Immediate |
| `job.get` | Poll receipt and progress without repeating work. Use job.result for output. | `jobHandle` | No | Immediate |
| `job.result` | Read a bounded JSON text page. Concatenate pages by offset to reconstruct full output. | `jobHandle` | No | Immediate |
| `job.wait` | Wait up to 120 seconds for an existing job without resubmitting work. Timeout returns its current receipt with waitTimedOut=true. Cancelling this wait does not cancel the job. No request key required. | `jobHandle` | No | Wait |

## Runtime

| Operation | Purpose | Required arguments | Grant | Execution |
| --- | --- | --- | --- | --- |
| `runtime.get_state` | Read the explicit target runtime state over ADS without loading a solution. | `amsNetId` | No | Job |
| `runtime.list_symbols` | Enumerate runtime symbols over ADS with prefix/substring filters and a bounded result count. includeTypes adds type and size. | `amsNetId` | No | Job |
| `runtime.ping_target` | Probe system and runtime ADS reachability with per-probe timeoutMs; distinguish unreachable, rebooting and reachable targets. | `amsNetId` | No | Job |
| `runtime.read_plc_log` | Listen to the TwinCAT event log over ADS for a bounded window, optionally filtering messages. Requires the TcEventLogger proxy. | `amsNetId` | No | Job |
| `runtime.read_var` | Read one PLC symbol over ADS, returning value, data type and byte size. | `symbol`, `amsNetId` | No | Job |
| `runtime.read_var_list` | Read up to 500 PLC symbols in a single ADS batch and return structured values. | `amsNetId`, `symbols` | No | Job |
| `runtime.record` | Record PLC variables through ADS notifications into CSV with optional start/stop triggers. No TE13xx license is needed; recording and trigger waits are bounded by arguments. | `amsNetId`, `variables` | No | Job |
| `runtime.set_state` | Change the explicit target runtime state over ADS. Requires a grant for this operation and AMS target. | `state`, `amsNetId`, `grantHandle` | Yes | Job |
| `runtime.write_var` | Write one PLC symbol over ADS. Requires a grant for this operation and AMS target. | `symbol`, `value`, `amsNetId`, `grantHandle` | Yes | Job |
| `runtime.write_var_list` | Write up to 500 PLC symbol/value pairs in a single ADS batch. Requires a grant for this operation and target. | `amsNetId`, `variables`, `grantHandle` | Yes | Job |

## Safety

| Operation | Purpose | Required arguments | Grant | Execution |
| --- | --- | --- | --- | --- |
| `safety.grant` | Grant specific operations on exactly one target, context or Scope config for a fixed lifetime. Calling host must obtain user authorization; a grant is not authentication. | `operations`, `reason` | No | Job |
| `safety.revoke` | Revoke a grant immediately without a request key. Subsequent workflow steps recheck it; already dispatched effects are not undone. | `grantHandle` | No | Immediate |

## Scope

| Operation | Purpose | Required arguments | Grant | Execution |
| --- | --- | --- | --- | --- |
| `scope.create_config` | Create a .tcscopex file from explicit target, variables and recording settings. Requires a TE13xx-enabled worker. | `amsNetId`, `variables` | No | Job |
| `scope.export` | Export an existing Scope recording through TC3ScopeExportTool. Requires TE13xx; writes the requested output file. | `inputPath` | No | Job |
| `scope.start` | Start a Scope Server recording from an existing .tcscopex file. Requires a grant scoped to that configPath; returns recordingHandle on success. | `configPath`, `grantHandle` | Yes | Job |
| `scope.status` | Read the status of the recording identified by recordingHandle. Never creates a new Scope process. | `recordingHandle` | No | Job |
| `scope.stop` | Stop the recording identified by recordingHandle and export its data. Returns path, elapsed time and sample count. | `recordingHandle` | No | Job |

## System

| Operation | Purpose | Required arguments | Grant | Execution |
| --- | --- | --- | --- | --- |
| `system.reap_orphans` | Clean dead-parent workers recorded in session files using PID start-time verification. | None | No | Job |
| `system.route_remove` | Remove one exact ADS route locally, or from both systems when removeRemote is true. Remote removal requires a DPAPI-protected PSCredential file. | `amsNetId`, `confirm`, `grantHandle` | Yes | Job |
| `system.route_upsert` | Create or replace one fingerprint-verified self-signed Secure ADS route using Beckhoff TcXaeMgmt, then verify the target ADS port. Reads the target credential from a DPAPI-protected PSCredential file; plaintext passwords are not accepted. | `amsNetId`, `ipOrHostName`, `name`, `credentialPath`, `fingerprint`, `confirm`, `grantHandle` | Yes | Job |
| `system.routes` | List configured ADS routes through Beckhoff TcXaeMgmt, including Secure ADS mode, certificate fingerprint and current online status. | None | No | Job |
| `system.status` | Application version and queue status. Does not start TwinCAT. | None | No | Immediate |

## Workflow

| Operation | Purpose | Required arguments | Grant | Execution |
| --- | --- | --- | --- | --- |
| `workflow.build_verify` | Build and verify expected TMC symbols and/or types in one operation. Compilation success and artifact verification are reported separately. Requires explicit expectations; preserves last-good artifacts on failure. | `contextHandle` | No | Job |
| `workflow.deploy` | Build, prepare the boot project, activate configuration and restart the context target. Returns the native deployment receipt. dryRun previews the workflow. | `contextHandle`, `grantHandle`, `confirm` | Yes | Job |
| `workflow.sequence` | Run up to 32 validated catalog operations in order, stopping on first failure. Each step includes its handles. No rollback, nesting or replay. | `steps` | No | Job |
| `workflow.test` | Build, configure the test task and boot project, optionally disable I/O, activate, restart and poll TcUnit results on the context target. Requires a grant even for local targets. | `contextHandle`, `grantHandle`, `confirm` | Yes | Job |
| `workflow.wait_state` | Poll runtime state until expectedState or timeout; returns final observation and attempt count. | `amsNetId`, `expectedState` | No | Job |
