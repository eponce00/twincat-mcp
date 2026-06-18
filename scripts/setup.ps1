# First-time setup script
# Usage: .\scripts\setup.ps1

$ErrorActionPreference = "Stop"

Write-Host "=== TwinCAT MCP Server Setup ===" -ForegroundColor Cyan
Write-Host ""

# Check prerequisites
Write-Host "Checking prerequisites..." -ForegroundColor Yellow

# Check Visual Studio / MSBuild
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vswhere) {
    $vsPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
    if (-not $vsPath) {
        $vsPath = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild -property installationPath
    }
    if ($vsPath) {
        $msbuild = Join-Path $vsPath "MSBuild\Current\Bin\MSBuild.exe"
        if (Test-Path $msbuild) {
            Write-Host "✅ MSBuild found: $msbuild" -ForegroundColor Green
        } else {
            Write-Host "❌ MSBuild.exe not found in VS installation" -ForegroundColor Red
            exit 1
        }
    } else {
        Write-Host "❌ Visual Studio with MSBuild not found" -ForegroundColor Red
        Write-Host "   Install Visual Studio 2022 (or newer) with '.NET desktop development' workload" -ForegroundColor Gray
        exit 1
    }
} else {
    Write-Host "❌ Visual Studio Installer not found" -ForegroundColor Red
    Write-Host "   Install Visual Studio 2022 Community (free) or newer" -ForegroundColor Gray
    exit 1
}

# Check .NET Framework targeting pack
$targetingPackPath = "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2"
if (Test-Path $targetingPackPath) {
    Write-Host "✅ .NET Framework 4.7.2 Targeting Pack installed" -ForegroundColor Green
} else {
    Write-Host "❌ .NET Framework 4.7.2 Targeting Pack not found" -ForegroundColor Red
    Write-Host "   Download from: https://dotnet.microsoft.com/download/dotnet-framework" -ForegroundColor Gray
    exit 1
}

# Check Python
try {
    $pythonVersion = python --version 2>&1
    Write-Host "✅ Python: $pythonVersion" -ForegroundColor Green
} catch {
    Write-Host "❌ Python not found. Please install Python 3.10+." -ForegroundColor Red
    exit 1
}

# Check if TwinCAT is installed (look for TcXaeShell registry)
$tcInstalled = $false
try {
    $tcKey = Get-ItemProperty -Path "HKLM:\SOFTWARE\WOW6432Node\Beckhoff\TwinCAT3" -ErrorAction SilentlyContinue
    if ($tcKey) {
        $tcInstalled = $true
        Write-Host "✅ TwinCAT 3 detected" -ForegroundColor Green
    }
} catch {}

if (-not $tcInstalled) {
    Write-Host "⚠️  TwinCAT 3 not detected in registry (may still work if installed)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Building TcAutomation..." -ForegroundColor Yellow

# Build using MSBuild (NOT dotnet - COM references require MSBuild.exe)
$projectPath = Join-Path $PSScriptRoot "..\TcAutomation\TcAutomation.csproj"
& $msbuild $projectPath /p:Configuration=Release /p:Platform=x64 /restore /v:minimal

if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ Build failed" -ForegroundColor Red
    exit 1
}
Write-Host "✅ TcAutomation built successfully" -ForegroundColor Green

Write-Host ""
Write-Host "Installing Python dependencies..." -ForegroundColor Yellow

Push-Location "$PSScriptRoot\..\mcp-server"
pip install -r requirements.txt
if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ pip install failed" -ForegroundColor Red
    Pop-Location
    exit 1
}
Pop-Location
Write-Host "✅ Python dependencies installed" -ForegroundColor Green

Write-Host ""
Write-Host "=== Setup Complete! ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "1. Open this folder in VS Code"
Write-Host "2. Restart VS Code to load the MCP server"
Write-Host "3. In Copilot Chat, try: 'Build my TwinCAT project at C:\path\to\solution.sln'"
Write-Host ""
Write-Host "To test manually:" -ForegroundColor Yellow
Write-Host "  .\scripts\test-cli.ps1 -Solution 'C:\path\to\solution.sln'"
Write-Host "  .\scripts\test-mcp.ps1  # Opens MCP Inspector"
