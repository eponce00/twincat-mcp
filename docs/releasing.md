# Releasing

The application version is defined by `VERSION` in
`mcp-server/twincat_mcp/engine.py`; the MCP server and `system.status`
report that value. MCP protocol and dependency versions are separate.

Use a major application version for incompatible public tools, operation
contracts or result semantics. Version 2.0.0 is a breaking application release.

## Preparation

1. Set the application version and update the matching section in
   [CHANGELOG.md](../CHANGELOG.md), including upgrade requirements.
2. Update the README, architecture, operation index and affected workflow guides.
   Keep product documentation focused on supported behavior and reproducible
   instructions.
3. Install development dependencies and run:

   ```powershell
   .\.venv\Scripts\python.exe -m pip install -r mcp-server/requirements-dev.txt
   .\.venv\Scripts\ruff.exe check mcp-server
   .\scripts\test-mcp-automated.ps1
   .\scripts\build.ps1
   git diff --check
   ```

4. Qualify hardware-dependent features on a designated test target or explicitly
   disclose unqualified integrations in the release notes.
5. Review and commit the release contents. Replace the unreleased status in the
   changelog with the publication date when publishing.

## Publication

Create the matching `vMAJOR.MINOR.PATCH` tag on the reviewed commit and publish
release notes from the changelog. A build or a documentation update does not
publish a release.

For native artifacts, `scripts/publish.ps1` builds and copies the executable
and its dependencies to `TcAutomation/publish`. This script prepares local
artifacts only. Keep DLLs and configuration files together; Python server
sources and pinned requirements must also be available to the installation.
