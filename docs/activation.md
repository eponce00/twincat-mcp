# Activation completion

`engineering.activate`, `workflow.deploy` and `workflow.test` call the XAE
Automation Interface `ActivateConfiguration()`. Earlier versions then slept a
fixed time (5 s, 10 s for `workflow.test`). `workflow.deploy` and
`workflow.test` then restarted TwinCAT.

On a local user-mode runtime (UmRT), XAE 3.1.4026 can report a successful
activation that never reached the target. It has already replaced the PLC boot
files (`Boot\Plc\Port_*.app`), but `Boot\CurrentConfig.xml` still holds the
previous project. A restart then starts the PLC against the wrong process image.
Port 851 logs "The loaded symbol description does not match the plc
application", and the user-mode system service can stop with an access
violation. In observed deploys, the first activation after loading a solution
did not reach the runtime, and a second activation did.

## Behavior

The three operations share one implementation
(`TcAutomation/Core/ActivationWait.cs`). It records a baseline before the
first activation, then polls every 0.5 s.

| Target | Verification | Result |
| --- | --- | --- |
| Boot folder on this PC | `CurrentConfig.xml` differs from the baseline (write time/length or `TcBootProject/@CreateTime`), parses as a complete boot project, and it and the files written with it (`CurrentConfig.tszip`, `CurrentProjectInfo.json`, `Plc\*.app`) stay unchanged for 3 s. A readable system service (ADS port 10000) must be in Config, Run or Stop for 3 s. | `verified:true`, `signal:"bootConfig"` |
| Remote | A landed activation leaves the system service state and restart index unchanged, so ADS shows no completion signal. The wait requires the system service to be settled for 3 s. | `verified:false`, `signal:"systemState"`, with a warning |
| Nothing readable | A fixed 20 s delay. | `verified:false`, `signal:"none"`, with a warning |

A change to the PLC boot files alone never counts: XAE writes them even when
the activation does not land.

- **Second attempt (local targets only).** If `CurrentConfig.xml` has not
  changed at all 30 s after an activation, the activation did not reach the
  target. The same `ActivateConfiguration()` call is dispatched once more and
  verified again. This is the documented manual recovery. A rewrite already in
  progress is never re-dispatched. Remote targets are not retried, because the
  outcome cannot be observed there.
- **Timeout.** The final attempt waits up to 120 s. Override this with
  `TWINCAT_MCP_ACTIVATION_TIMEOUT_SECONDS` (clamped to 10–1800 s) in the
  environment of the MCP server or CLI. On timeout the operation fails, names
  the missing signal and does **not** restart TwinCAT. The error also warns that
  the target may already hold the new PLC boot files next to the previous
  configuration, so it must not be restarted until an activation has landed.
- **Older targets.** If a target answers only a plain `ReadState` (no system
  service restart index), that state is used to judge whether it has settled.

Boot folders are found by matching the target AMS Net ID against each
user-mode runtime's `Runtimes\*\3.1\TcRegistry.xml` under
`%ProgramData%\Beckhoff\TwinCAT\3.1`. A local XAR is matched through the
TwinCAT registry key (`AmsNetId`/`BootDir`), and `127.0.0.1.1.1` also maps to
it. Any other target is treated as remote.

## Receipts

Native results gain an `activation` object (`Activation` in PascalCase CLI
output). `workflow.deploy` step 7 also records `verified`, `signal`, `attempts`,
`elapsedMs` and `warning`. Progress lines (`[PROGRESS] activate: …`) report the
baseline, the wait state every 10 s, any second attempt and the outcome.

```json
{
  "completed": true,
  "verified": true,
  "signal": "bootConfig",
  "attempts": 2,
  "elapsedMs": 36600,
  "bootDirectory": "C:\\ProgramData\\Beckhoff\\TwinCAT\\3.1\\Runtimes\\UmRT_Default\\3.1\\Boot\\",
  "bootConfigRewritten": true,
  "systemStateBefore": "Run (restart index 60)",
  "systemStateAfter": "Run (restart index 60)",
  "bootConfigBefore": "CurrentConfig.xml written …, CreateTime 2026-11-05T19:13:51",
  "bootConfigAfter": "CurrentConfig.xml written …, CreateTime 2026-11-05T19:19:40",
  "observations": ["attempt 1: +0.0s system state Run (restart index 60)", "…"]
}
```

## Validation

`scripts/test-plc-objects.ps1` runs the scripted-probe cases in
`TcAutomation.Tests/ActivationWaitTests.cs`. They cover a landed activation in
Run, a PLC-only rewrite, a second attempt that lands, bounded retries, churning
and partial boot files, unsettled and unreadable system state, remote and
ReadState-only targets, the unverified fallback, boot folder discovery and
timeout bounds. No ADS connection, XAE or PLC is used.

The live behavior was qualified on a local UmRT (XAE/XAR 3.1.4026.26). See the
changelog entry for 3.0.1. Local XAR registry discovery and remote targets have
not been qualified on a bench.
