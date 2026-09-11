# Contributing

Issues and focused pull requests are welcome.

## Before making a change

Use the prerequisites and setup instructions in the [README](README.md#install). The C# automation executable requires MSBuild; Python hosts the MCP server.

For changes to public interfaces or architecture, document the problem and proposed behavior in a focused design note. Keep pull requests focused on one change and update the affected documentation and examples.

## Validation

Build with `scripts/build.ps1` and run the relevant tests in `mcp-server/tests`. Integration scripts are available under `scripts/test-*.ps1`; inspect their target and operations before running them. Record which tests ran and whether TwinCAT or hardware integration was exercised.

## Reporting an issue

Include a minimal reproduction, expected and actual behavior, relevant versions, and sanitized logs. Remove credentials, private project code, and sensitive machine or network details before posting.

## Pull requests

Explain the problem, the resulting behavior, and how you validated it. Call out compatibility changes and any validation that remains incomplete.

## Releases

Follow the [release procedure](docs/releasing.md). Update the [changelog](CHANGELOG.md)
and public documentation with interface changes. Application and MCP protocol
versions are tracked separately.
