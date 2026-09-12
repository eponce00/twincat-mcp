# Online change through MCP v2

Use `twincat_describe` to inspect the operations below, then invoke them through
`twincat_execute`. Every submitted operation needs a unique requestKey and returns
a job receipt. Keep the returned handles explicitly.

1. Execute `context.open` with the absolute solutionPath, target amsNetId,
   plcName and optional tcVersion. Retain contextHandle. Use exclusive engineering
   access to the solution and target, including across other XAE applications.
2. Obtain a `safety.grant` scoped to that context for
   `engineering.set_target`, `engineering.matching_login`,
   `engineering.edit_plc_source` and `engineering.online_change`, after user
   authorization. Retain grantHandle. Set the target if needed.
3. Execute `engineering.matching_login` with contextHandle, grantHandle,
   port, matching configuration/platform and `confirm:"CONFIRM"`.
   Keep the original source and compile information; do not clean or edit before login.
4. Read the POU with `engineering.read_plc_source`, supplying contextHandle,
   its Automation Interface path and section (`declaration` or `implementation`).
   The result contains text and SHA256 computed from its exact UTF-8 representation.
5. Execute `engineering.edit_plc_source` with those fields, replacement text,
   expectedSha256 and grantHandle. A stale hash rejects the edit. The operation
   saves source but does not apply it to the runtime.
6. Execute `engineering.check_all_objects`. After successful compilation, read
   `TwinCAT_SystemInfoVarList._AppInfo.OnlineChangeCnt` from the explicit runtime
   target, then execute `engineering.online_change`.
7. Check application behavior separately. Close the context when finished.

Example execute arguments for the online-change step:

```json
{
  "operation": "engineering.online_change",
  "arguments": {
    "contextHandle": "<context handle>",
    "grantHandle": "<grant handle>",
    "port": 851,
    "contextFile": "C:\\absolute\\path\\to\\the-online-plc\\MAIN.TcPOU",
    "cycleSymbol": "MAIN.cycles",
    "expectedOnlineChangeCount": 0,
    "confirm": "CONFIRM"
  },
  "requestKey": "<new UUID>"
}
```

The selected PLC must be the only logged-in PLC in the owned engineering session.
`contextFile` must be an existing absolute source path inside the selected online
PLC application. The edited object may live in a referenced source-library project.
The worker activates the application editor before dispatch because XAE resolves
the Online Change target from the active PLC editor context.
The command checks target and configured ADS port, engineering login/RUN, command
availability, ADS RUN and advancing cycles. An unrelated interactive XAE session
does not provide this context. Multiple simultaneous PLC logins are rejected.

Matching login rejects change/download/overwrite prompts rather than approving
replacement applications. Unknown or localized dialog variants may time out.
A worker timeout is an uncertain outcome; inspect it before another operation.

`cycleSymbol` must be a monotonically increasing ULINT updated by the application
cyclic task. Success requires exactly one online-change counter increment and
advancing cycles afterward. That is evidence of application, not proof of retained
application state, uninterrupted execution or correct machine behavior.

`timeoutMs` bounds post-dispatch ADS verification (100–60000 ms, default 10000).
It does not bound the synchronous XAE COM call. Online change never substitutes
login, download, activation, restart or a boot-project update.

Receipts retain dispatchAttempted, dispatched, runtimeVerified and outcomeUnknown
where provided by the native command. After response loss, resend the same
requestKey and arguments to recover the receipt; no new dispatch occurs.
An intentional retry uses a new key only after reconciling runtime/source state.
Source save failures may leave an edit applied; cancellation does not roll it back.

Qualify this sequence on a designated test target before production use.
Automated protocol and mocked-backend tests do not establish hardware compatibility.

[Beckhoff's Online Change reference](https://infosys.beckhoff.com/content/1033/tc3_userinterface/2531444363.html)
describes retained compile information and changes requiring a full download.
