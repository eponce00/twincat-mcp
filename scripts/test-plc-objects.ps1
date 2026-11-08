# Native PLC object policy tests. Uses in-memory project nodes; no XAE/PLC access.
param(
    # Build with TcXaeShell only, through build-no-vs.ps1.
    [switch]$NoVisualStudio,
    # Passed to build-no-vs.ps1 when TcXaeShell is not in its default folder.
    [string]$MsBuildPath
)
$ErrorActionPreference = 'Stop'
if ($MsBuildPath -and -not $NoVisualStudio) { throw '-MsBuildPath applies only with -NoVisualStudio.' }
$taskRoot = (Resolve-Path "$PSScriptRoot/..").Path
if ($NoVisualStudio) {
    $noVsArgs = @{ Tests = $true }
    if ($MsBuildPath) { $noVsArgs.MsBuildPath = $MsBuildPath }
    & "$PSScriptRoot/build-no-vs.ps1" @noVsArgs
} else {
    $taskVsWhere = "${env:ProgramFiles(x86)}/Microsoft Visual Studio/Installer/vswhere.exe"
    if (-not (Test-Path -LiteralPath $taskVsWhere)) { throw 'Visual Studio MSBuild is required.' }
    $taskVsPath = & $taskVsWhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
    if (-not $taskVsPath) { throw 'Visual Studio MSBuild is required.' }
    $taskMsBuild = Join-Path $taskVsPath 'MSBuild/Current/Bin/MSBuild.exe'
    & $taskMsBuild "$taskRoot/TcAutomation.Tests/TcAutomation.Tests.csproj" /p:Configuration=Release /p:Platform=x64 /restore /v:minimal
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
& "$taskRoot/TcAutomation.Tests/bin/Release/TcAutomation.Tests.exe"
exit $LASTEXITCODE
