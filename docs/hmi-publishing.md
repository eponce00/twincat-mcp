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

| Operation | Behavior |
| --- | --- |
| `hmi.inspect` | Read selected-project readiness, SDK version and profile names. |
| `hmi.build` | Build only that HMI project, in Release. Requires a context grant. |
| `hmi.preflight` | Validate a named profile and return a source/config/toolchain fingerprint. |
| `hmi.publish` | Require the matching fingerprint and a scoped grant; call the native publisher once. |
| `hmi.status` | Read native progress/result and this context's last submission identity. Never republishes. |

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
