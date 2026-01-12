<p align="center">
  <img src="img/banner.png" alt="TwinCAT MCP Server" width="800"/>
</p>

<h1 align="center">TwinCAT MCP Server</h1>

<p align="center">
  <strong>Connect AI assistants to TwinCAT automation</strong><br>
  Build, deploy, and monitor TwinCAT PLCs directly from VS Code with GitHub Copilot
</p>

<p align="center">
  <a href="#features">Features</a> •
  <a href="#installation">Installation</a> •
  <a href="#usage">Usage</a> •
  <a href="#available-tools">Tools</a> •
  <a href="#troubleshooting">Troubleshooting</a>
</p>

---

## What is this?

An **MCP (Model Context Protocol) server** that enables AI assistants like GitHub Copilot to interact with TwinCAT XAE and PLCs. Ask Copilot to build your project, deploy to a PLC, read variables, or check system state - all through natural language.

> ⚠️ **Unofficial**: This is a community project and is not affiliated with or endorsed by Beckhoff Automation.

---

## Features

### 🔨 Build & Validate
- **Build Solutions** - Compile projects and get detailed error/warning reports with file paths and line numbers
- **Check All Objects** - Compile all objects including unused ones (catches errors in unreferenced function blocks)
- **Static Analysis** - Run static code analysis with naming conventions and coding rules (requires TE1200 license)
- **Project Info** - Get TwinCAT version, Visual Studio version, PLC list, and configuration details
- **Clean** - Remove build artifacts

### 🚀 Deployment
- **Set Target** - Configure target AMS Net ID for deployment
- **Activate** - Download configuration to target PLC
- **Restart** - Start/restart TwinCAT runtime
- **Deploy** - Full deployment workflow (build → set target → activate → restart)

### 📡 ADS Communication (No Visual Studio Required)
- **Get State** - Read TwinCAT runtime state (Run/Config/Stop)
- **Set State** - Switch between Run/Config/Stop modes
- **Read Variable** - Read PLC variables by symbol path
- **Write Variable** - Write values to PLC variables

### ⚙️ Configuration Management
- **List PLCs** - List all PLC projects with AMS ports
- **Boot Project** - Configure boot project autostart settings
- **Disable I/O** - Enable/disable I/O devices (for testing without hardware)
- **Variants** - Get/set project variants (TwinCAT 4024+)
- **List Tasks** - Show real-time tasks with cycle times and priorities
- **Configure Task** - Enable/disable tasks, set autostart
- **Configure RT** - Set real-time CPU cores and load limits

---

## Safety Features

This MCP server includes multiple safety mechanisms to prevent accidental damage to production PLCs.

### 🔒 SAFE/ARMED Mode

Dangerous operations require explicitly **arming** the server first:

```
"Arm dangerous operations for deploying hotfix to line 3"
```

- Server starts in **SAFE mode** - destructive tools are blocked
- Call `twincat_arm_dangerous_operations` to enable dangerous tools
- Armed mode **auto-expires after 5 minutes** (configurable)
- Call with `disarm: true` to manually return to safe mode

**Dangerous tools (require armed mode):**
- `twincat_activate` - Downloads config to PLC
- `twincat_restart` - Restarts TwinCAT runtime  
- `twincat_deploy` - Full deployment workflow
- `twincat_set_state` - Changes PLC state (Run/Stop/Config)
- `twincat_write_var` - Writes to PLC variables

### ✅ Confirmation Required

The most destructive operations require an additional `confirm: "CONFIRM"` parameter:

- `twincat_activate`
- `twincat_restart`
- `twincat_deploy`

This provides a two-step safety check: arm first, then confirm.

### 🏷️ Tool Annotations

All tools include MCP protocol annotations to help AI assistants understand risk levels:

| Annotation | Meaning |
|------------|---------|
| `readOnlyHint: true` | Tool only reads data, no modifications |
| `destructiveHint: true` | Tool can cause significant changes |
| `idempotentHint: true` | Safe to retry, same result each time |

### ⚙️ Configuration

| Environment Variable | Default | Description |
|---------------------|---------|-------------|
| `TWINCAT_ARMED_TTL` | `300` | Armed mode timeout in seconds (5 min) |

---

## Prerequisites

| Software | Version | Notes |
|----------|---------|-------|
| **Windows** | 10/11 | Required for COM interop |
| **Visual Studio** | 2019/2022 | With ".NET desktop development" workload |
| **.NET Framework** | 4.7.2 | [Developer Pack](https://dotnet.microsoft.com/download/dotnet-framework/net472) |
| **TwinCAT XAE** | 3.1.4024+ | [Beckhoff Downloads](https://www.beckhoff.com/) |
| **Python** | 3.10+ | Check "Add to PATH" during install |
| **VS Code** | Latest | With GitHub Copilot extension |

---

## Installation

### Quick Setup (Recommended)

```powershell
git clone https://github.com/eponce92/twincat-mcp.git
cd twincat-mcp
.\setup.bat
```

This will:
1. ✅ Check all prerequisites
2. ✅ Build TcAutomation.exe with MSBuild
3. ✅ Install Python dependencies
4. ✅ Register MCP server in VS Code globally (using `--add-mcp` CLI)

### Manual Installation

If you prefer to install manually or the setup script doesn't work:

```powershell
# Build the project
.\scripts\build.ps1

# Install Python dependencies
pip install -r mcp-server/requirements.txt

# Register with VS Code (or VS Code Insiders)
code --add-mcp '{"name":"twincat-automation","type":"stdio","command":"python","args":["C:/path/to/twincat-mcp/mcp-server/server.py"]}'
```

### Start the Server

1. **Restart VS Code** (or `Ctrl+Shift+P` → "Developer: Reload Window")
2. Press `Ctrl+Shift+P` → **"MCP: List Servers"**
3. Click **"twincat-automation"** to start
4. Click **"Start"** and **"Trust"** if prompted

---

## Usage

Once installed, the TwinCAT tools work in **any VS Code workspace**.

### Example Commands in Copilot Chat

```
"Build my TwinCAT project at C:\Projects\MyMachine\Solution.sln"

"Deploy to PLC at 192.168.1.10.1.1"

"Read MAIN.bRunning from the PLC"

"What's the TwinCAT state on 172.18.236.100.1.1?"

"Disable I/O devices and activate to the test PLC"

"List all tasks in my project"
```

### Example Outputs

**Build with errors:**
```
❌ Build failed (2 errors, 1 warning)

🔴 Errors:
  • POUs/MAIN.TcPOU:4 - C0077: Unknown type: 'DINT2'
  • POUs/FB_Motor.TcPOU:15 - C0035: Program name expected

🟡 Warnings:
  • GVLs/GVL_Main.TcGVL:8 - C0371: Unused variable 'nTemp'
```

**PLC State:**
```
🟢 TwinCAT State: Run
📡 AMS Net ID: 172.18.236.100.1.1
📊 Device State: 1
📝 Description: Run - Running normally
```

**Read Variable:**
```
✅ Variable Read: MAIN.bRunning
📊 Value: True
📋 Data Type: BOOL
📐 Size: 1 bytes
```

---

## Available Tools

### Safety Control
| Tool | Description |
|------|-------------|
| `twincat_arm_dangerous_operations` | Arm/disarm dangerous operations (required before deploy, activate, restart, etc.) |

### Build & Project Management
| Tool | Description |
|------|-------------|
| `twincat_build` | Build solution, return errors/warnings with line numbers |
| `twincat_get_info` | Get TwinCAT version, VS version, PLC list |
| `twincat_clean` | Clean solution (remove build artifacts) |
| `twincat_check_all_objects` | Check all PLC objects including unused ones (catches errors in unreferenced FBs) |
| `twincat_static_analysis` | Run static code analysis (requires TE1200 license) |

### Deployment (⚠️ Require Armed Mode + Confirmation)
| Tool | Description |
|------|-------------|
| `twincat_set_target` | Set target AMS Net ID |
| `twincat_activate` | Activate configuration on target (→ Config mode) |
| `twincat_restart` | Restart TwinCAT runtime (→ Run mode) |
| `twincat_deploy` | Full deployment: build → activate → restart |

### ADS Communication
| Tool | Description |
|------|-------------|
| `twincat_list_routes` | List available ADS routes (PLCs) from TwinCAT router |
| `twincat_get_state` | Get runtime state via ADS (Run/Config/Stop) |
| `twincat_set_state` | Set runtime state via ADS (⚠️ requires armed mode) |
| `twincat_read_var` | Read PLC variable by symbol path |
| `twincat_write_var` | Write value to PLC variable (⚠️ requires armed mode) |

### Configuration
| Tool | Description |
|------|-------------|
| `twincat_list_plcs` | List PLC projects with AMS ports |
| `twincat_set_boot_project` | Configure boot project autostart |
| `twincat_disable_io` | Enable/disable I/O devices |
| `twincat_set_variant` | Get/set project variant |
| `twincat_list_tasks` | List real-time tasks |
| `twincat_configure_task` | Enable/disable task, set autostart |
| `twincat_configure_rt` | Configure RT CPU cores and load limit |

---

## Troubleshooting

### MCP Server Not Starting

1. Press `Ctrl+Shift+P` → **"MCP: List Servers"**
2. Click "twincat-automation" → **"Start"**
3. If prompted, click **"Trust"**

### Build Error: "MSB4803: ResolveComReference not supported"

You're using `dotnet build` instead of MSBuild. Use the setup script:
```powershell
.\setup.bat
```

### "TwinCAT/Visual Studio not found"

Specify the TwinCAT version explicitly:
```
Build my project with TwinCAT version 3.1.4026.17
```

### ADS Connection Failed

- Verify AMS Net ID is correct
- Ensure ADS route exists to target
- Check firewall allows ADS traffic (port 48898)

---

## Project Structure

```
twincat-mcp/
├── setup.bat               # One-click setup
├── TcAutomation/           # .NET CLI tool (COM automation)
│   ├── Commands/           # Build, Deploy, ADS commands
│   ├── Core/               # VS instance, COM wrappers
│   └── Models/             # JSON output models
├── mcp-server/             # Python MCP server
│   └── server.py           # MCP protocol implementation
└── scripts/                # PowerShell helpers
    ├── setup.ps1           # Prerequisites + build
    ├── install-mcp.ps1     # Register with VS Code
    └── test-*.ps1          # Test scripts
```

---

## Development

### Adding New Tools

1. Add command class in `TcAutomation/Commands/`
2. Register in `Program.cs`
3. Add tool definition and handler in `mcp-server/server.py`
4. Test with `.\scripts\test-mcp-automated.ps1`

### Building

```powershell
.\scripts\build.ps1
```

### Testing

```powershell
# Full test suite
.\scripts\test-mcp-automated.ps1

# Quick test (skip build)
.\scripts\test-mcp-automated.ps1 -SkipBuild
```

---

## License

MIT License - See [LICENSE](LICENSE) for details.

---
