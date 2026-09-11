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
session and edit separately. A cold MCP host without a matching login will reject
the operation. Automatic baseline login/edit preparation remains a separate
engineering-workflow task.

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
