"""
twincat_mcp — modular internals for the TwinCAT MCP server.

The outer `server.py` file is the entry point the MCP client loads.
This package holds the reusable pieces it wires together:

- safety        armed-mode gating for destructive tools
- formatting    human-readable duration / timing output
- cli           TcAutomation.exe discovery + one-shot subprocess runners
- host          the persistent `TcAutomation.exe host` subsystem
                  (ShellHost class, singleton accessor, graceful shutdown)
- dispatch      run_shell_step — unified "run one step through host or CLI"
- tools.schemas the 26 Tool() descriptors for list_tools()

Nothing in this package imports `server`, so circular-import risk is zero.
"""
