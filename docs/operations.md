# Operation catalog

The three public MCP tools expose the following 51 operations. Use
`twincat_describe` for current full input schemas, defaults, limits, prerequisites
and examples. This index summarizes the catalog; it does not replace validation.

Submitted operations return jobs and require a request key. Immediate operations
return directly without a request key. Each marked operation requires a scoped grant.

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
| `engineering.build` | Build the solution and return structured errors, warnings and timing. Set clean:false to preserve compile information for online change. | `contextHandle` | No | Job |
| `engineering.check_all_objects` | Compile all PLC objects, including unused objects, and return structured diagnostics. | `contextHandle` | No | Job |
| `engineering.clean` | Delete generated build artifacts from the solution. Invalidates retained compile information needed for online change. | `contextHandle` | No | Job |
| `engineering.configure_rt` | Configure isolated real-time CPU count and load limit in the solution. | `contextHandle`, `grantHandle` | Yes | Job |
| `engineering.configure_task` | Enable or disable a named real-time task or configure its autostart setting. | `taskName`, `contextHandle`, `grantHandle` | Yes | Job |
| `engineering.disable_io` | Enable or disable top-level I/O devices in the solution configuration. | `contextHandle`, `grantHandle` | Yes | Job |
| `engineering.edit_plc_source` | Edit and save one POU section only if expectedSha256 matches. Requires the existing logged-in engineering context. Does not compile or apply the edit. | `path`, `section`, `text`, `expectedSha256`, `contextHandle`, `grantHandle` | Yes | Job |
| `engineering.generate_library` | Build and export the context PLC as a .library artifact. Existing outputs are backed up; dryRun previews the export. | `contextHandle` | No | Job |
| `engineering.get_error_list` | Read the XAE Error List with severity and substring filters; waitSeconds permits a bounded delay for async messages. | `contextHandle` | No | Job |
| `engineering.info` | Read solution, toolchain and PLC project information in the engineering context. | `contextHandle` | No | Job |
| `engineering.list_plcs` | List PLC projects, configured ADS ports and boot-project settings in the solution. | `contextHandle` | No | Job |
| `engineering.list_tasks` | List configured real-time tasks with cycle times, priorities and enabled state. | `contextHandle` | No | Job |
| `engineering.matching_login` | Log into the unchanged baseline in this engineering context. Requires retained compile information, matching configuration/platform, configured ADS port and a RUN application. Rejects download/change prompts. | `port`, `configuration`, `platform`, `confirm`, `contextHandle`, `grantHandle` | Yes | Job |
| `engineering.online_change` | Apply one online change in the already logged-in matching PLC context. Verify one counter increment and advancing cycles. Never substitute download, activate, restart or boot-project update. | `port`, `cycleSymbol`, `expectedOnlineChangeCount`, `confirm`, `contextHandle`, `grantHandle` | Yes | Job |
| `engineering.read_plc_source` | Read a POU declaration or implementation and its exact UTF-8 SHA256 from the engineering context. | `path`, `section`, `contextHandle` | No | Job |
| `engineering.restart` | Restart the TwinCAT runtime on the context target. Requires an operation-scoped grant and CONFIRM. | `contextHandle`, `grantHandle`, `confirm` | Yes | Job |
| `engineering.set_boot_project` | Configure autostart and generate the selected PLC boot project on the context target. | `contextHandle`, `grantHandle` | Yes | Job |
| `engineering.set_target` | Set the solution target to the AMS Net ID fixed by context.open. Does not activate. | `contextHandle`, `grantHandle` | Yes | Job |
| `engineering.set_variant` | Get or set the solution variant. Requires TwinCAT XAE 4024 or newer; omit variantName to query. | `contextHandle`, `grantHandle` | Yes | Job |
| `engineering.static_analysis` | Run TE1200 static analysis and return rule violations. Requires the appropriate license. | `contextHandle` | No | Job |

## Job

| Operation | Purpose | Required arguments | Grant | Execution |
| --- | --- | --- | --- | --- |
| `job.cancel` | Cancel queued work or stop before the next workflow step. Dispatched commands may finish; poll receipt. | `jobHandle` | No | Immediate |
| `job.get` | Poll receipt and progress without repeating work. Use job.result for output. | `jobHandle` | No | Immediate |
| `job.result` | Read a bounded JSON text page. Concatenate pages by offset to reconstruct full output. | `jobHandle` | No | Immediate |

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
| `system.routes` | Read configured ADS routes from local TwinCAT StaticRoutes.xml without contacting a PLC. | None | No | Job |
| `system.status` | Application version and queue status. Does not start TwinCAT. | None | No | Immediate |

## Workflow

| Operation | Purpose | Required arguments | Grant | Execution |
| --- | --- | --- | --- | --- |
| `workflow.deploy` | Build, prepare the boot project, activate configuration and restart the context target. Returns the native deployment receipt. dryRun previews the workflow. | `contextHandle`, `grantHandle`, `confirm` | Yes | Job |
| `workflow.sequence` | Run up to 32 validated catalog operations in order, stopping on first failure. Each step includes its handles. No rollback, nesting or replay. | `steps` | No | Job |
| `workflow.test` | Build, configure the test task and boot project, optionally disable I/O, activate, restart and poll TcUnit results on the context target. Requires a grant even for local targets. | `contextHandle`, `grantHandle`, `confirm` | Yes | Job |
| `workflow.wait_state` | Poll runtime state until expectedState or timeout; returns final observation and attempt count. | `amsNetId`, `expectedState` | No | Job |
