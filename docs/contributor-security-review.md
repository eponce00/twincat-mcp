# Contributor security and architecture review

Review date: 2026-10-28. Baseline: application 2.3.0 (`1985014`).
The five contributor PRs below were already merged; remediation is included in
application 3.0.0. The review evaluates code and observable behavior, without
assuming how it was authored.

| Contribution | Disposition |
| --- | --- |
| [#1: library export](https://github.com/eponce00/twincat-mcp/pull/1) | Retain the capability; replace speculative API probing, correct the installation flag, protect outputs and enforce grants. |
| [#2: tspproj support](https://github.com/eponce00/twincat-mcp/pull/2) | Retain the parser change. Both project GUID/extension combinations are covered by native tests. |
| [#3: ADS batch and Scope](https://github.com/eponce00/twincat-mcp/pull/3) | Retain and harden ADS batching/recording and Scope configuration creation. Withdraw incomplete Scope capture/export behavior. |
| [#7: XAE ownership](https://github.com/eponce00/twincat-mcp/pull/7) | Retain launch locking, exact-PID ROT attachment, COM identity comparison and version verification. Pin identity throughout UI callbacks too. |
| [#8: session janitor](https://github.com/eponce00/twincat-mcp/pull/8) | Retain strict fingerprints and native-handle termination. Replace the remaining compare/delete race with conditional deletion on the same locked file handle. |

## Findings and corrections

### Unintended repository installation and speculative native calls

The library exporter supplied `true` to `SaveAsLibrary` while labeling it an
overwrite flag. Beckhoff documents the argument as
[installation into the default repository](https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242730891.html).
It also searched for save/export methods and tried multiple argument sets after
failures. That can introduce unintended effects or repeat a partially completed
operation.

Export now calls only `ITcPlcIECProject.SaveAsLibrary(path, false)` on the selected
nested PLC project. Unsupported interfaces fail explicitly. Native failures are
not retried with guessed flags. Preview performs no build or export. Default
export checks all objects, builds, stages the result, publishes a new `.library`
file and returns its hash. Existing outputs remain untouched.

### Partial ADS failures reported as overall success

Batch result types lacked explicit overall success, allowing failed item results
to pass through generic success inference. A workflow could therefore advance
after failed writes. Permissive BOOL conversion could silently turn an invalid
value into `false`. Comma-delimited transport also damaged multidimensional
symbol names, and recursive normalization changed symbol dictionary keys.

Batch results now fail on any item error, retain partial results and preserve
case-sensitive symbol keys. Writes that lose their outcome after dispatch are
marked uncertain. BOOL accepts only `TRUE`, `FALSE`, `1`, `0`; parsing and size
checks precede dispatch. Read/record symbol arrays use JSON. Individual symbol
data is capped at 1 MiB and batch symbol data at 8 MiB. Batch writes are not
atomic and are not rolled back. The batch API bundles client work; it does not
claim a single ADS sum transaction.

### Unbounded or misleading recording data

The ADS recorder accumulated rows without an aggregate memory bound, guessed
numeric interpretations for unsupported types, and could emit initial zeros for
channels whose first notification had not arrived. Artifact writers could
truncate existing files without their own operation grant.

Recording is restricted to declared numeric scalar types and waits for a value
from every channel before producing snapshots. It retains at most 100,000 rows
and 1,000,000 cells. Exhaustion stops capture and fails with actual collected
rows retained. CSV headers are quoted and spreadsheet formula prefixes escaped.
Values are represented as doubles; large 64-bit integers can lose precision.
These are latest-per-channel snapshots, not atomic simultaneous PLC samples.

Library export, ADS recording and Scope configuration creation require scoped
grants. Output paths must be absolute, use the expected extension, avoid reparse
points and create a new file atomically. Existing files are never silently
backed up, renamed or overwritten by these operations.

### Incomplete Scope capture/export

Scope stop could substitute an empty CSV when export failed and estimate sample
counts from elapsed time. Its saved configuration was not proof of exported
recorded data. The external export wrapper also read process output before its
claimed timeout and relied on unqualified command syntax.

Those implementations are removed. `scope.start`, `scope.stop`, `scope.status`
and `scope.export` are disabled, omitted from search, explicitly marked
`available:false` by describe, and rejected before dispatch. Native capture/export
entry points fail without generating data. Scope configuration creation remains
optional and does not claim recording functionality. Use bounded ADS recording
for supported numeric data. Re-enabling Scope requires documented native APIs,
real acquired-data export, immutable configuration authorization, process cleanup
and designated-bench qualification.

### Process and record identity races

A PID-only dialog registration can outlive its process. UI callbacks now verify
start-time identity and retain a process handle for their complete duration.
Unknown or mismatched identities authorize no clicks.

The janitor previously reread a record and then deleted its pathname. A writer
could replace the file in that gap. Conditional deletion now holds one native
file handle, excludes replacement/writes during the identity comparison and
marks that same file for deletion. Replacement records are retained. Process
termination still requires exact start-time verification on a pinned native
handle; unrelated or unverified processes remain untouched.

## Dependency review

The resolved environment contained matching reviewed advisories for PyJWT,
certifi, OpenTelemetry API, pip and setuptools. Updates pin PyJWT 2.15.0,
certifi 2026.10.18, OpenTelemetry API/SDK 1.45.1, pip 26.2.2 and setuptools
84.2.1. The SDK/API pair is aligned. Setup updates packaging tools before
installing runtime requirements.

Examples of the reviewed advisories are
[PyJWT key confusion](https://github.com/advisories/GHSA-ffc3-869f-jxw9),
[certifi trust roots](https://github.com/advisories/GHSA-6q48-6c7w-9gw2),
[OpenTelemetry parsing](https://github.com/advisories/GHSA-xcq7-p3pf-p88h), and
[setuptools Windows path escape](https://github.com/advisories/GHSA-xmj3-9gf7-h52w).
The JWT/HTTP-specific findings do not establish an exploit in the stdio server.
After remediation, queries for 66 resolved Python/NuGet package versions returned
no matching reviewed GitHub advisories, with no query errors. This is a dated
known-advisory check, not proof that dependencies have no vulnerabilities.

## Verification and limits

- 91 Python protocol, orchestration and lifecycle tests passed, including new
  grant, unavailable-integration, partial-result and symbol transport checks.
- Native MSBuild passed: 55 object-policy, 24 feedback/janitor, 40 relocation
  and 43 review regressions. Tests exercise actual conditional record deletion
  during concurrent publication, output preservation, strict conversion,
  recording limits and process identity without PLC writes.
- Real stdio/XAE 3.1.4026.26 qualification verified non-installing library export,
  no-build previews, hash evidence and existing-output rejection in a disposable
  project. No PLC login, activation or download was performed.
- CI runs Python checks on Windows with Python 3.10 and 3.13, read-only
  permissions, no persisted checkout credentials and SHA-pinned actions.
  Native/vendor qualification remains a separate Windows bench requirement.

No malicious payload, hidden external service or new arbitrary-code endpoint
was identified in the reviewed contribution paths. This does not establish
intent or guarantee the absence of other defects. The server remains a local,
trusted stdio service: the host owns user consent, and engineering projects,
COM registrations and installed toolchains must be trusted. Grants enforce
operation scope; they are not authentication or a multi-user network boundary.
