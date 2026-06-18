# Build TcAutomation.exe
# Usage: .\scripts\build.ps1
#
# NOTE: This project uses classic .csproj format with COM references.
# It MUST be built with MSBuild.exe (Visual Studio), NOT dotnet CLI.
# The dotnet CLI cannot resolve COM references (MSB4803 error).

$ErrorActionPreference = "Stop"

Write-Host "Building TcAutomation..." -ForegroundColor Cyan

# Find MSBuild from Visual Studio
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) {
    Write-Host "❌ vswhere.exe not found. Is Visual Studio installed?" -ForegroundColor Red
    exit 1
}

$vsPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
if (-not $vsPath) {
    $vsPath = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild -property installationPath
}
if (-not $vsPath) {
    Write-Host "❌ Visual Studio with MSBuild not found." -ForegroundColor Red
    Write-Host "   Install Visual Studio 2022 (or newer) with '.NET desktop development' workload." -ForegroundColor Gray
    exit 1
}

$msbuild = Join-Path $vsPath "MSBuild\Current\Bin\MSBuild.exe"
if (-not (Test-Path $msbuild)) {
    Write-Host "❌ MSBuild.exe not found at: $msbuild" -ForegroundColor Red
    exit 1
}

Write-Host "Using MSBuild: $msbuild" -ForegroundColor Gray

$projectPath = Join-Path $PSScriptRoot "..\TcAutomation\TcAutomation.csproj"

& $msbuild $projectPath /p:Configuration=Release /p:Platform=x64 /restore /v:minimal

if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "✅ Build succeeded!" -ForegroundColor Green
    Write-Host "Executable: TcAutomation\bin\Release\TcAutomation.exe" -ForegroundColor Gray
} else {
    Write-Host "❌ Build failed with exit code $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}
