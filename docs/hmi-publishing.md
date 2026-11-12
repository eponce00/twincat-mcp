# Native HMI publishing

The HMI path uses TE2000's `TcHmiAutomation.dll` and
`DTE.GetObject("Beckhoff.TcHmi.1.12")`. Its public `ITcHmiProject` API supplies
Build, GetPublishProfile, Publish, IsPublishRunning and GetPublishResult. The
interface is read from the installed SDK; the vendor assembly is not redistributed
or required for ordinary PLC operations.

## Ownership and operations

Open `context.open_hmi` with an existing absolute `.sln`, an exact absolute
`.hmiproj` in that solution, and the expected HTTPS server root URL. This is a
separate context kind from `context.open`: it needs no PLC name, AMS Net ID or
TwinCAT project. It shares the existing exclusive, owned hidden XAE worker and
serialized job queue. PLC operations cannot run in an HMI context, and HMI
operations cannot run in a PLC context. Close it with `context.close`.

The engineering server of the worker uses ports 3000 and 13000. If another process holds
them, the worker picks other ports. It then writes them into the `.hmiproj` on an HMI build
and on exit (measured, TE2000 1.12.762.57). The server also writes them into the top-level
`ENDPOINTS` of `Server/TcHmiSrv/TcHmiSrv.Config.default.json` when the project loads. A later
load with the ports free writes 3000 and 13000 back. Once its shell has exited, the worker
restores each `.hmiproj` of the solution whose only change is those two ports. In the server
configuration, it restores only the original `ENDPOINTS` array. The ports of its
`http://127.0.0.1` elements must be the only change in it. Any other change stays, a mapping
refresh included. Measured on `context.close` only.

A running engineering server holds `.engineering_servers/<name>/TcHmiSrv.lock` next to the
solution (measured). No lock file was left after `context.close`. If any lock there is still
held, the worker restores no ports. It keeps the locks open while it restores. Measured with a
lock held across `context.close` (ports busy): both files kept the replacement ports.

The engineering server can start a copy of itself from `%TEMP%`, which outlives the shell. The
worker starts an HMI shell inside a Windows job. After the shell exits, it ends every process
left in that job (measured on `context.close`, five runs). Processes started
outside the worker are not touched. A process that breaks away from the job is not ended. If
members survive one retry, the worker lets the job go so that the next shell can start. It then
restores no engineering ports, because a surviving server can still write them (assumed, not
measured). The worker holds the only handle to the job, and the job ends its members when that
handle closes. So if the worker dies, Windows ends the shell and every member (measured: worker
killed mid-refresh, no process of the run left three seconds later).

| Operation | Behavior |
| --- | --- |
| `hmi.inspect` | Read selected-project readiness, SDK version and profile names. |
| `hmi.build` | Build only that HMI project, in Release. Requires a context grant. |
| `hmi.preflight` | Validate a named profile and return a source/config/toolchain fingerprint. |
| `hmi.publish` | Require the matching fingerprint and a scoped grant; call the native publisher once. |
| `hmi.status` | Read native progress/result and this context's last submission identity. Never republishes. |
| `hmi.refresh_mappings` | Refresh ADS PLC symbol mappings from the live PLC. Requires a context grant. |

Use the existing three MCP tools: search, describe and execute. These are catalog
operations, not new public tools. Category `hmi` is available in search.

## Publish sequence

1. Open an HMI context and inspect its project and profile names.
2. Grant `hmi.build` and `hmi.publish` on that context for the approved server.
3. Build the HMI. A build does not publish it.
4. Run preflight with `profileName`. Check its project, target, configuration and
   deletion flags. Retain `fingerprint`.
5. Publish with that profile, `expectedFingerprint`, context/grant handles and
   `confirm: "CONFIRM"`. Use a unique request key; reuse that same key and the
   exact arguments if the response is lost.
6. Wait on the returned durable job. Inspect status if the result is uncertain.
   Close the context when finished.

The publisher uses the profile's existing extension/configuration selection,
TLS settings, locally saved credentials and server permissions. It accepts no
password or certificate override. Profile values are never changed to force a
publication. The native SDK's `IsValidPublishProfile` validates the profile;
preflight does not prove server connectivity, credentials or licensing.

The selected profile must match the context URL, preserve files and extension
data, and abort on errors. HTTP, URL credentials, paths, queries and fragments
are refused. Deleting the server's existing files or data requires a separate
explicit workflow; this publisher does not offer that option.

## Fingerprint and completion

The fingerprint binds the exact project path, target URL, selected profile,
project source/configuration and the installed automation/publishing SDK files.
Encrypted local publishing credentials participate only as file bytes in the
hash; their values are not returned. Correcting or rotating credentials therefore
requires a new preflight.

Source traversal is bounded to 20,000 entries, 32 MiB per file and 256 MiB in
aggregate. Generated output, package caches and editor state are excluded. This
first version refuses dynamic Content inputs and links outside the HMI root;
bring shared application assets into the project or a packaged HMI dependency.
It does not fingerprint the contents of restored package caches. Protect and pin
those dependencies as part of the engineering environment.

A matching preflight is checked immediately before dispatch. It is not an atomic
snapshot against external editors or native generators. `sourceChangedDuringPublish`
records a changed fingerprint after dispatch, including metadata the native
publisher may save. A successful result proves native publish completion, not
that a browser rendered correctly or that the process equipment is commissioned.

On the qualified SDK, `GetPublishResult()` returns a Boolean. An unknown result
shape fails with `outcomeUnknown`; it is not interpreted as success. A submission
ID identifies the MCP attempt. The native status may retain an earlier result,
so use `hasSubmission` and the submission ID when interpreting it.

There is no automatic retry, file-copy fallback, PLC activation or runtime restart.
An in-flight COM call cannot be forcibly rolled back. A deadline bounds polling
and worker waiting; the native Publish call itself may return late. After an
uncertain reply, only status reading is allowed in that context. If its worker
was lost, no replacement worker is opened to guess the outcome. Inspect the target
and durable receipt before starting a new operation.

Vendor exception text can contain sensitive data. Results identify the failed
stage instead of returning raw exception text or credential-bearing diagnostics.

## Mapping refresh

A changed PLC type leaves a stale cached `SCHEMA` in `TcHmiSrv.Config::SYMBOLS`, or a stale
struct in `DEFINITIONS`. The designer's *Refresh mapping* unmaps each selected symbol and maps
it again with `AddSymbol`. `hmi.refresh_mappings` does the same through the public SDK, one
symbol at a time. `ITcHmiProject.RefreshSymbols()` refreshes data types only and is not used.

1. Select directly mapped ADS PLC symbols (`MAPPING` of the form `PLC1::GVL::x`). Extension
   functions, TMC and sub-symbol mappings are skipped. A mapping with an unparsed target or a
   runtime not enabled in the ADS configuration is skipped and named. `symbols` limits the selection.
2. Before any HMI call, connect to each runtime named in the domain's
   `*.Config.default.json` and read symbol metadata, never values. A missing target is skipped
   and reported. An unreachable runtime, an unverified target or the `timeoutSeconds` deadline
   stops the operation.
3. Per symbol, write the original entry to a recovery record first. The record lives in
   `hmi-recovery` under the MCP state folder, one per configuration file. If it cannot be
   written, stop before the unmap. Then call `UnMapSymbol` (never removing historized data),
   confirm the entry is gone, and call `MapSymbol`. Of `ACCESS`, `HIDDEN`, `OPTIONS`, `CUSTOMERDATA`, `DYNAMIC` and
   `USEMAPPING`, set each one the original entry had back to its original value. If `MapSymbol`
   fails, write the original entry back and stop. The deadline is checked before each symbol.
4. Read every outcome back from the persisted configuration, on success and on a stop. A
   touched symbol must match its expected entry, once, and a remapped entry must keep its planned
   `DOMAIN` and `MAPPING`. Any other changed symbol or user group, a duplicated definition, or a
   section present as a non-object fails the operation. On a
   stop, so does a definition changed since the last verified remap. A touched entry that changed
   after its verification returns `outcomeUnknown`. A run with nothing to refresh fails with
   `hmi_mapping_nothing_selected`.

A failed restore or an interrupted call returns `outcomeUnknown` and invalidates the context.
The result names that symbol and whether its entry was restored. A refresh changes the
fingerprint. Run `hmi.build` and `hmi.preflight` again before publishing.

### Recovery

A dead shell or worker can stop a run between an unmap and its remap. The mapping is then gone,
and a later refresh cannot select it. The recovery record keeps its original entry:

- Once the mapping is present as verified or as it was, the record is cleared. After an
  interrupted call, the record is always kept, because the server can still be working on it.
- Otherwise the result carries `recovery` with the symbol, its `originalEntry` and `recordPath`.
  The record stays on disk. It also survives a dead worker.
- `context.open_hmi` lists open records of that project as `pendingRecovery`. It never acts on them.
- While a record is open, a refresh fails with `hmi_mapping_recovery_pending`. The exception is a
  mapping that is back exactly as it was. With its definitions present, the refresh clears the
  record and proceeds. With definitions missing, the refresh must include that mapping. It then
  remaps it first, which regenerates the definitions and settles the record.
- A record names the worker that wrote it, by process ID and start time. While that worker runs,
  no other worker clears or recovers the record (`hmi_recovery_owner_running`). A dead worker's
  shell and servers are already gone, because its job ended with it.
- A refresh and a recovery of the same configuration hold an exclusive lock file next to the
  record. A second worker gets `hmi_mapping_busy`. If the holding worker dies, Windows releases the lock.
- `hmi.refresh_mappings` with `recover: true` (and no `symbols`) settles the record:

  | Mapping now | Recover mode |
  | --- | --- |
  | Absent | Writes the original entry back. |
  | Bound to the original `DOMAIN` and `MAPPING` | Sets the preserved fields back (a remap that finished). |
  | Identical to the original | Writes nothing. |
  | Another target, or the name more than once | Changes nothing (`hmi_recovery_conflict`). |

  Every branch reads the symbol back, once and as written. To clear the record, no other symbol
  or user group may have changed, and every server definition the entry refers to must be present.
- For a conflict, an unreadable record, or a record of another project, resolve the mapping by
  hand in the designer. Then delete the file named in `recordPath`.
- Refresh the recovered mapping again afterwards, so that its schema is current.

Measured on TE2000 1.12.762.57, on a scratch project copy with its PLC in Run:

- A scalar mapping seeded with a wrong cached `SCHEMA` got the PLC type back. Its seeded
  `ACCESS`, `HIDDEN` and `OPTIONS` values and its user-group reference stayed.
- A struct definition seeded with a missing property got it back and matched the original.
- A mapping to a missing PLC symbol was skipped as `target_missing` and left unchanged.
- A refresh without `symbols` remapped 109 mappings in 19.6 s and skipped only the missing
  target. Besides the seeded struct, 81 definitions changed. In 73, only generated function
  names changed. Six were new types and two had changed. Every checked change matched the
  current PLC source.
- The refreshed configuration stayed semantically identical through a reopen and an `hmi.build`.
- On an unseeded copy, a refresh, `hmi.build`, `hmi.preflight` and `hmi.publish` to a licensed
  remote TF2000 server succeeded. The publish took 52.5 s, and `hmi.status` reported
  `lastResult: true`.
- The profile file changed between the end of `hmi.preflight` and the end of the publish. It
  gained `socketTimeout`, and its port changed from a string to a number. No other project file
  outside `bin`, `obj` and `.vs` changed. The publish reported `sourceChangedDuringPublish: true`.
- That publish ran with the engineering ports busy, so the published configuration listed
  `ENDPOINTS` `http://127.0.0.1:13002` and `:3002`. The profile has
  `alwaysPublishLocalServerConfiguration: true`. A day later the remote server's configuration
  page still listed its own endpoints (`https://0.0.0.0:1020`, `http://0.0.0.0:1010`). So in this
  one observation, the publish did not write the engineering endpoints to the remote server.

Failure paths, measured on 2026-11-12 on scratch copies against the same PLC:

- `timeoutSeconds: 12` stopped a full run with `hmi_mapping_deadline` after 70 mappings. No
  mapping was missing, and the context stayed usable.
- The PLC project was activated during a run. `MapSymbol` failed for the 37th mapping, the worker
  wrote its entry back, and the run stopped with `hmi_map_failed`, `restored: true`. No mapping
  was missing, and the record was cleared.
- The XAE shell was killed during a run. The result was `hmi_mapping_interrupted` with
  `outcomeUnknown: true`, and the in-flight mapping was gone. A new context reported it as
  `pendingRecovery`, and `recover: true` wrote back a byte-identical entry.
- The worker was killed during a run. Its shell and engineering servers were gone three seconds
  later. Recovery from a new worker restored the entry the same way.
- Staged records on a scratch copy covered each recover branch:
  - Absent: restored byte-identical.
  - Same target with other fields: preserved fields set back.
  - Another target: record kept.
  - Identical: cleared by a refresh.
  - A missing struct definition: regenerated by a refresh that remapped the mapping first.
  - An owner process still running: refused.

Measured with the PLC unreachable, `UnMapSymbol` persists at once. A failed `MapSymbol` leaves
the mapping deleted, and writing the original entry restores it exactly.

## Qualification

Qualified with TE2000 14.18.57.1 on Windows/XAE, against a local TF2000 server.
The actual stdio/native path exercised project inspection, profile target refusal,
HMI build, source fingerprint refusal, successful publication, duplicate-key
replay of the receipt, status and closure. No screen automation or PLC restart
was used. The HIL PLC continued running at Max.

Repeat on an explicitly approved server with:

```powershell
python scripts/qualify-hmi.py --solution C:/Projects/Machine/Hmi.sln `
  --project C:/Projects/Machine/Hmi/Panel.hmiproj `
  --target-url https://127.0.0.1:1020/ --profile LocalHil `
  --publish --output C:/Checks/hmi-qualification.json
```

Omit `--publish` for build/preflight-only qualification. Use a new output file;
existing receipts are not overwritten. The optional local `Administrator` profile
check in the script applies to the documented LocalHil fixture, not every project.

Other SDK versions need installation-specific qualification, especially their
result shape. Missing TE2000 fails HMI operations without breaking PLC support.
