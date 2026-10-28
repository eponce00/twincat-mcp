# Contributing

Issues and focused pull requests are welcome.

## Before making a change

Use the prerequisites and setup instructions in the [README](README.md#install). The C# automation executable requires MSBuild; Python hosts the MCP server.

For changes to public interfaces or architecture, document the problem and proposed behavior in a focused design note. Keep pull requests focused on one change and update the affected documentation and examples.

## Validation

Build with `scripts/build.ps1` and run the relevant tests in `mcp-server/tests`. Run `scripts/test-plc-objects.ps1` for the native in-memory object-policy harness; it builds with MSBuild and does not start XAE or access a PLC. Integration scripts are available under `scripts/test-*.ps1`; inspect their target and operations before running them. Record which tests ran and whether TwinCAT or hardware integration was exercised.

## Reporting an issue

Include a minimal reproduction, expected and actual behavior, relevant versions, and sanitized logs. Remove credentials, private project code, and sensitive machine or network details before posting.

## Pull requests

Explain the problem, the resulting behavior, and how you validated it. Call out compatibility changes and any validation that remains incomplete.

## Releases

Follow the [release procedure](docs/releasing.md). Update the [changelog](CHANGELOG.md)
and public documentation with interface changes. Application and MCP protocol
versions are tracked separately.

## Safety and architecture review

Changes are reviewed by behavior and evidence, regardless of the authoring tool.

- Keep the three MCP tools fixed; add bounded catalog operations and task-focused
  workflows with explicit schemas, prerequisites and effects.
- Require grants for PLC mutations and artifact writers. Enforce context/target
  scope before dispatch and stop sequences on any failed step.
- Use documented APIs and named parameters. Do not guess COM methods or retry
  an uncertain mutating call with different flags. Preserve uncertain receipts.
- Report observed results. Never substitute placeholder recordings, inferred
  sample counts or file existence for verified acquisition/export success.
- Use atomic new-file creation for artifacts. Preserve existing files and
  validate paths, extensions, sizes, duration and aggregate resource limits.
- Pin process identity for termination and UI automation. A PID alone is
  insufficient. Unknown identity must leave the process/window untouched.
- Cover negative paths: missing/wrong grants, partial failures, cleanup races,
  process reuse, output collisions and external edits. Tests must not write to
  a PLC unless the target and actions are explicitly authorized.
- Qualify vendor integrations on a designated bench. Mocked tests and a passing
  default build do not qualify optional SDK code; keep incomplete paths disabled.

The pull-request workflow runs Python contract/security regressions on Windows
with read-only repository permissions and SHA-pinned actions. Native changes
also require the MSBuild harness and installation-specific XAE qualification.
Do not use `pull_request_target` to execute contributor code with secrets or
write tokens. Record dependency advisory checks when adding/updating packages.
