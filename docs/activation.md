# Activation completion

`engineering.activate`, `workflow.deploy` and `workflow.test` call the XAE
Automation Interface `ActivateConfiguration()`. That call can return before the
target has switched to Config mode and written its new boot configuration. On a
local user-mode runtime (UmRT) this takes 10–15 s. Earlier versions slept a
fixed 5 s (10 s for `workflow.test`) and then restarted TwinCAT. On a slow
target the restart could therefore load the previous `Boot\CurrentConfig.xml`.
The PLC then reported that the loaded symbol description did not match the
application, and the runtime could crash.

## Behavior

The three operations share one wait (`TcAutomation/Core/ActivationWait.cs`).
Before dispatching the activation, the wait records a baseline. Afterwards it
polls every 0.5 s until every readable signal confirms completion.

| Signal | Targets | Complete when |
| --- | --- | --- |
| Boot configuration | Targets whose boot folder is on this PC | `CurrentConfig.xml` differs from the baseline (file stamp or `TcBootProject/@CreateTime`), parses as a complete boot project, and it and the files written with it (`CurrentConfig.tszip`, `CurrentProjectInfo.json`, `Plc\*.app`) stay unchanged for 3 s. |
| System service state | All targets (ADS port 10000) | The state or restart index has changed from the baseline, and the state has stayed in Config, Run or Stop for 3 s. |

The boot folder is found by matching the target AMS Net ID against each
user-mode runtime's `Runtimes\*\3.1\TcRegistry.xml` under
`%ProgramData%\Beckhoff\TwinCAT\3.1`. A local XAR is matched through the
TwinCAT registry key (`AmsNetId`/`BootDir`), and `127.0.0.1.1.1` also maps to
it. Any other target is treated as remote and uses only the system-service
signal.

- **Timeout.** The default is 180 s. Override it with
  `TWINCAT_MCP_ACTIVATION_TIMEOUT_SECONDS` (clamped to 10–1800 s) in the
  environment of the MCP server or CLI. On timeout, the operation fails with the
  missing signal named, and TwinCAT is **not restarted**.
- **Fallback.** If port 10000 is unreadable and there is no local boot folder,
  the wait sleeps a fixed 20 s and reports the activation as unverified
  (`verified:false`, `signal:"none"`, with a warning). If only the boot
  configuration is readable, it alone verifies completion, with a warning.
- **Older targets.** If a target answers only a plain `ReadState` (no system
  service restart index), a state change still counts.

## Receipts

Native results gain an `activation` object (`Activation` in PascalCase CLI
output):

```json
{
  "completed": true,
  "verified": true,
  "signal": "bootConfig+systemState",
  "elapsedMs": 13500,
  "bootDirectory": "C:\\ProgramData\\Beckhoff\\TwinCAT\\3.1\\Runtimes\\UmRT_Default\\3.1\\Boot\\",
  "systemStateBefore": "Run (restart index 60)",
  "systemStateAfter": "Config (restart index 61)",
  "bootConfigBefore": "CurrentConfig.xml written …, CreateTime 2026-11-05T18:32:12",
  "bootConfigAfter": "CurrentConfig.xml written …, CreateTime 2026-11-05T19:04:51",
  "observations": ["+0.0s system state Run (restart index 60)", "…"]
}
```

`workflow.deploy` step 7 also records `verified`, `signal` and `elapsedMs`.
Progress lines (`[PROGRESS] activate: …`) report the baseline and, every 10 s,
the current wait state.

## Validation

`scripts/test-plc-objects.ps1` runs the scripted-probe cases in
`TcAutomation.Tests/ActivationWaitTests.cs`: the 12 s UmRT rewrite, stale and
churning boot files, stuck/unreadable system state, remote restart-index and
state-only targets, the unverified fallback, boot folder discovery and timeout
bounds. No ADS connection, XAE or PLC is used.
