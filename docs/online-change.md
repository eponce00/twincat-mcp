# Online Change

`twincat_online_change` applies a delta in the persistent host's already logged-in
PLC project. Use exclusive engineering access to the solution and target. Keep
the compile information from the matching download; do not clean the project.
The selected PLC must be the only logged-in PLC in that owned session, and the
Online Change command must be available. Multiple simultaneous PLC logins are
rejected because XAE does not reliably expose PLC editor ownership through DTE.
An unrelated interactive XAE session cannot supply this context.

Required arguments:

```json
{
  "solutionPath": "C:/work/Machine/Machine.sln",
  "amsNetId": "1.2.3.4.1.1",
  "plcName": "PLC",
  "port": 851,
  "cycleSymbol": "MAIN.cycles",
  "expectedOnlineChangeCount": 0,
  "confirm": "CONFIRM"
}
```

`cycleSymbol` must be a monotonically increasing `ULINT` incremented by the
application's cyclic task. Read the expected counter from
`TwinCAT_SystemInfoVarList._AppInfo.OnlineChangeCnt` immediately before the call.
The operation requires the existing armed-operation gate. `timeoutMs` bounds the
post-dispatch ADS verification period (100–60000 ms, default 10000); it does not
bound the synchronous XAE COM command. A host timeout is an uncertain outcome.

The command checks target and configured ADS port, engineering login/RUN,
sole online PLC context, command availability, ADS RUN and advancing cycles.
Success requires exactly one online-change counter increment and advancing
cycles afterward. This is runtime application evidence, not proof of uninterrupted
execution, retained application state or correct machine behavior. Test those
separately with application-specific assertions.

The operation does not log in, edit source, build the whole solution, download,
activate, restart, update the boot project or approve dialogs. Prepare the matching
session and edit separately. A cold MCP host without a matching login rejects
the operation.

## Prepare and edit in the persistent session

1. Open/select the solution and target with `twincat_set_target`. Keep the matching
   source and compile information; do not clean or change source before login.
2. Arm operations and call `twincat_matching_login` with `solutionPath`, `amsNetId`,
   `plcName`, `port`, matching `configuration`/`platform` (for example `Release` /
   `TwinCAT RT (x64)`) and `confirm: "CONFIRM"`. It requires the configured port
   and a running application. An offline session may report no reference code;
   a successful login must report unchanged code afterward. It temporarily disables silent
   dialog defaults and cancels change/download/overwrite prompts in its owned XAE
   process. It never approves a replacement application. Unknown/localized dialog
   variants may time out; inspect the session instead of retrying automatically.
3. Call `twincat_read_plc_source` with the same solution/target/PLC plus the full
   Automation Interface POU `path` and `section` (`declaration` or `implementation`).
   The result contains `Text` and `Sha256`, calculated over the exact UTF-8 text.
4. Call `twincat_edit_plc_source` with those fields, replacement `text` and the
   returned `expectedSha256`. The existing matching host and a logged-in PLC are
   required. A stale hash rejects the edit. It saves source, but does not apply it.
5. Run `twincat_check_all_objects`, then explicitly call `twincat_online_change`
   with the current expected runtime count. Check application behavior separately.

Use the same `tcVersion` selection throughout. Login and edits never fall back to
a one-shot process after host failure. Read the current source/runtime to reconcile
an uncertain outcome. A failed edit can have changed source if its save/reply failed;
do not assume rollback. Login and edit are also dispatcher steps `matching-login`
and `edit-plc-source`; source reads use `read-plc-source`.

An isolated build can be selected with `TWINCAT_AUTOMATION_EXE` in the MCP server
environment. An invalid explicit path fails instead of selecting another build.

On the dedicated TcForge bench (XAE 3.1.4026.26, XAR 3.1.4026.17), an actual MCP
client completed this sequence on ADS 854: matching login, source read, rejected
stale hash, edit, object check and Online Change (runtime count 0 to 1, cycles
31365 to 32146). Source was restored and the owned session closed. The object
check included one generated-TMC version warning; this is not a zero-warning or
production-load qualification. The TcForge tracker retains the evidence paths.

`DispatchAttempted`, `Dispatched`, `RuntimeVerified` and `OutcomeUnknown` distinguish
dispatch from verification. On a host error, the wrapper never falls back to a
new process or retries the command. Inspect runtime counters and application state
before deciding whether to retry. An unchanged boot project can still start the
previous application after reboot; update it as a separate explicit operation.

The same primitive is available as the `online-change` step in the C# dispatcher
and as `OnlineChangeCommand.ExecuteInSession` for an exclusively owned session.
Batch use requires the same armed/confirmation gates.

[Beckhoff Online Change reference](https://infosys.beckhoff.com/content/1033/tc3_userinterface/2531444363.html)
describes compile information, active project context and changes that require a
download. This tool never substitutes that download.
