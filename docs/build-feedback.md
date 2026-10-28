# Build feedback and artifact verification

Use `engineering.check_all_objects` for a syntax check that includes unused
objects. Use `engineering.build` for compilation/code generation. Use
`workflow.build_verify` when success also requires particular generated symbols
or data types. All three run through the existing engineering context; none
logs into, downloads to, activates or restarts a PLC.

## Build and verify

After saving source through the object operations, call `twincat_execute`:

```json
{
  "operation": "workflow.build_verify",
  "arguments": {
    "contextHandle": "<context handle>",
    "clean": false,
    "expectedSymbols": ["GVL_Diagnostics.nCount", "MAIN.*"],
    "expectedTypes": ["ST_Diagnostics"],
    "timeoutSeconds": 180
  },
  "requestKey": "<new UUID>",
  "waitSeconds": 120
}
```

At least one nonempty expectation array is required. Names are case-sensitive;
a trailing `*` matches a prefix. Each array accepts up to 200 names.
`engineering.build` accepts the same optional expectations. The compiler still
builds the solution; artifact expectations apply to the context's selected PLC.

The completed result separates `compilerSuccess` from overall `success`.
A compiler-successful build with a missing expectation returns `success:false`,
the missing names, and the artifact evidence. It does not manufacture a compiler
error. A failed compiler result cannot pass verification using an old TMC.

The worker saves pending source before dispatch. Build completion requires a
matching DTE build-begin/build-done event pair, rather than an earlier `Done`
state. Accessible output panes are cleared before dispatch and their subsequent
text is captured in `buildOutput`. This is a build-window capture; other IDE
messages arriving during that window can also appear.

`build` metadata identifies the run, UTC start/completion times, configuration,
platform, failed-project count and observed source revision. A source revision
is local to the loaded shell and tracks MCP source mutations; it is not a global
IDE change counter.

## Inspect generated content

```json
{
  "operation": "engineering.symbols",
  "arguments": {
    "contextHandle": "<context handle>",
    "kind": "symbols",
    "query": "GVL_Diagnostics",
    "offset": 0,
    "limit": 100
  },
  "requestKey": "<new UUID>"
}
```

`kind:"types"` indexes data types separately. `query` is a case-insensitive
substring filter. Pages contain up to 200 names and return `nextOffset` when
more remain. Names come from TMC entries; this does not recursively expand every
structured variable or enumerate a running PLC's ADS namespace. Use
`runtime.list_symbols` for the latter.

TMC paths come from the loaded TwinCAT project's PLC entries, with the adjacent
`.tmc` as fallback when no TMC path is configured. The worker does not select
an arbitrary matching file elsewhere in the solution. Each artifact reports
existence, SHA256, modification time, symbol/type counts and observed symbol
additions/removals. XML is bounded to 64 MiB and DTDs are prohibited.

| Artifact status | Meaning |
| --- | --- |
| `created`, `changed`, `refreshed`, `unchanged` | Comparison before/after an observed successful compiler run. An incremental build can legitimately leave identical bytes. |
| `retained_after_failed_build` | The compiler failed and the previous TMC bytes remain. |
| `changed_during_failed_build` | The TMC changed during a failed run; it cannot verify success. |
| `unverified_existing` | A file exists, but this context has not observed its build. |
| `changed_since_observed_build` | The current file differs from this context's recorded artifact. |
| `source_changed_since_observed_build` | The artifact matches, but the saved source manifest has changed. |
| `missing`, `unreadable` | No usable TMC can be inspected. |

The source manifest hashes the PLC project and its explicit `Compile` files,
bounded to 4,096 files and 64 MiB total. It can detect saved source changes made
outside MCP. It does not cover every library, toolchain setting, generated input,
unsaved editor change or external build. Unavailable comparison hashes are
reported as unknown. A matching hash is evidence of identical observed bytes,
not independent proof that the compiler regenerated the file.

Library-only projects may have no application TMC. Use `check_all_objects` for
their syntax and the library-export operation for the deliverable. Request
symbol verification only where application symbols are expected.

## Unused objects and linking policy

A saved, registered object can be absent from generated application content
because it is unused. Types and runtime variables are different; a type need
not produce an instantiated runtime variable. Missing TMC content alone cannot
establish a registration failure or an object's reference count.

`engineering.create_object` and `engineering.update_object` accept:

| `linkingPolicy` | Behavior |
| --- | --- |
| `preserve` (default) | Preserve the supplied/existing source policy. |
| `always` | Add a standalone `{attribute 'linkalways'}` pragma if absent. |
| `default` | Remove standalone `linkalways` pragmas and restore ordinary source linking. |

Explicit policy changes support programs, functions, function blocks and GVLs.
They are rejected for DUTs and members. An update may contain only the policy
and the usual object path/hash/grant; the declaration is read and updated by
the worker. These options edit source pragmas, not every IDE object-property
setting. They neither instantiate an FB nor guarantee particular runtime
symbols. Review generated code size and intended application use before opting
into always linking.

Reference code from the application when appropriate, check all objects, then
build with explicit expectations. The server does not force linking or reload
the project after every mutation. Offline qualification on XAE 3.1.4026.26
included immediate builds of new referenced and explicitly linked objects;
other toolchains and library-specific compiler behavior require their own checks.

Beckhoff documents [Check all objects](https://infosys.beckhoff.com/content/1033/tc3_userinterface/2531345035.html)
as checking unused objects without code generation, and describes
[linking policy](https://infosys.beckhoff.com/content/1033/tc3_plc_intro/2529686923.html)
and [object properties](https://infosys.beckhoff.com/content/1033/tc3_userinterface/3245438475.html).

## Diagnostics, clean and deadlines

The worker honors the Boolean returned by native `CheckAllObjects`. Native
compiler failure always produces a failed result and at least one error entry,
even if the Error List cannot provide detailed failures. Some XAE versions
report source diagnostics at the medium severity level during compiler failure.
The worker then classifies those source entries as errors, retaining
`rawSeverity` and `severityInferred:true`. This inference can include warnings
from the same failed run. Error List queries expose the last compiler-check
result and source revisions; the IDE list itself can retain older diagnostics.

`engineering.clean` invokes the IDE clean operation and reports retained TMC
paths. It does not promise that all generated files disappear. Cleaning also
invalidates compile information needed for online change; use `clean:false`
when retaining that baseline.

Build `timeoutSeconds` is 1–600 seconds, default 180. It covers preparation,
cleaning, compilation and feedback capture after solution selection. Shell
startup has its own bound. An expired deadline terminates only the shell owned
by this worker and returns `outcomeUnknown:true`; changes already saved are not
rolled back. The context expires and must be closed before opening another.
Lost native replies are never automatically replayed.

Execute can wait up to 120 seconds. For a still-running receipt, use `job.wait`
with its handle and a wait of up to 120 seconds, without a request key. A wait
timeout returns the current receipt with `waitTimedOut:true`. Request cancellation
releases the waiter, while the durable job continues. Use `job.cancel` to request
job cancellation and `job.result` to read large results in pages.

## Client and process lifetime

Stdio EOF cancels request waiters and closes the server's resources. A Windows
watchdog also monitors the client process through the Python virtual-environment
launcher using a pinned process handle. On confirmed client exit it starts
cleanup, with a default 60-second final process-exit deadline. Set
`TWINCAT_MCP_SHUTDOWN_GRACE_SECONDS` to 1–120 seconds to change that deadline, or
`TWINCAT_MCP_PARENT_WATCHDOG=0` to disable client monitoring.

No idle timeout closes healthy clients. A client that stays alive and retains
an abandoned pipe cannot be distinguished from a healthy idle connection by
parent monitoring alone. `system.status.lifecycle` reports the Python process,
parent and monitored-client PIDs and whether monitoring is enabled. A Windows
virtual environment normally has both a launcher and interpreter process.

The native host owns only its launched XAE instance. Launch ownership is
verified before attaching, and an unrelated IDE is neither adopted nor closed.
Session janitor records use strict timestamp fingerprints and retained native
handles for identity verification and termination. Malformed, inaccessible or
unverified records remain available for diagnosis. `system.reap_orphans`
reports cleaned, stopped and unresolved counts rather than assuming every
record was safely removed.
