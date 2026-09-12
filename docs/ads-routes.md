# Secure ADS route management

TwinCAT MCP manages workstation ADS routes through Beckhoff's `TcXaeMgmt`
PowerShell module. This API supports Secure ADS directly. Beckhoff's Automation
Interface also exposes the `TIRR` route tree through `ITcSmTreeItem.ConsumeXml`,
but the documented example creates an unencrypted route. The MCP uses the route
cmdlets so self-signed certificate verification is explicit.

## Prepare a protected credential

Create the credential file once, while signed in as the Windows account that
runs the MCP server:

```powershell
$credential = Get-Credential
$credential | Export-Clixml "$env:LOCALAPPDATA/TwinCAT/bench.credential.xml"
```

Windows DPAPI protects the password for that user on that computer. Pass only
the absolute file path to MCP. Do not put a password in an operation argument,
repository file or environment variable.

Obtain the target's SHA-256 self-signed certificate fingerprint from a trusted
view of that target, such as its TwinCAT About dialog or `TcSelfSigned.xml`.
`system.route_upsert` requires this value and passes it to Beckhoff's
`Add-AdsRoute -SelfSigned -FingerPrint` validation.

## Recovery flow

1. Call `system.routes` to inspect current route metadata and availability.
2. Create a target-scoped `safety.grant` covering `system.route_upsert` and, when
   replacement is needed, `system.route_remove`.
3. Remove the local entry with `system.route_remove`. Keep `removeRemote:false`
   when repairing only the engineering workstation.
4. Execute `system.route_upsert` with the target AMS Net ID, IP address or host
   name, route name, protected credential path, expected fingerprint, and ADS
   port to verify. The default verification port is the TwinCAT system service
   at `10000`.

The upsert result includes the installed route and a live ADS state probe. A
successful operation proves both route creation and communication with the
requested port. Removal from both systems is available with `removeRemote:true`
and requires the credential path.

Both mutations require an explicit scoped grant, `confirm:"CONFIRM"`, and a
unique request key. A timeout is recorded as an uncertain outcome and is never
automatically replayed.

Beckhoff references:

- [TwinCAT PowerShell `Add-AdsRoute`](https://infosys.beckhoff.com/content/1033/tc3_ads_ps_tcxaemgmt/11223580171.html)
- [Creating and handling routes with the Automation Interface](https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242939787.html)
- [Secure ADS self-signed certificates](https://infosys.beckhoff.com/content/1033/secure_ads/6798101003.html)
