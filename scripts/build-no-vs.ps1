<#
.SYNOPSIS
  Builds TcAutomation.exe on a machine with TcXaeShell and no Visual Studio.

.DESCRIPTION
  Uses the MSBuild inside TcXaeShell with the opt-in /p:NoVisualStudioBuild=true
  overlay (TcAutomation\Directory.Build.props and .targets). -Tests also builds
  the native test harness. See README "Build without Visual Studio".
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Platform = 'x64',
    [string]$MsBuildPath,
    [switch]$Tests
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $repo 'TcAutomation\TcAutomation.csproj'
$testProj = Join-Path $repo 'TcAutomation.Tests\TcAutomation.Tests.csproj'

# 1. MSBuild 15+ (TcXaeShell ships 15.9).
if ($MsBuildPath -and -not (Test-Path -LiteralPath $MsBuildPath -PathType Leaf)) { throw "MsBuildPath not found: $MsBuildPath" }
$msbuildCandidates = @(
    $MsBuildPath,
    "${env:ProgramFiles(x86)}\Beckhoff\TcXaeShell\MSBuild\15.0\Bin\MSBuild.exe",
    "${env:ProgramFiles(x86)}\Beckhoff\TcXaeShell\MSBuild\Current\Bin\MSBuild.exe"
) | Where-Object { $_ }
$msbuild = $msbuildCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $msbuild) {
    throw "No MSBuild found. Searched:`n  $($msbuildCandidates -join "`n  ")`nInstall TcXaeShell, or pass -MsBuildPath."
}
Write-Host "Using MSBuild: $msbuild" -ForegroundColor Gray

# 2. The output folder, read from the csproj (Release builds to bin\Release-v2).
#    Supports literal, unconditioned OutputPath elements in unconditioned or
#    '$(Configuration)|$(Platform)' groups of the csproj itself.
$xml = [xml](Get-Content -LiteralPath $proj -Raw)
$want = "'`$(Configuration)|`$(Platform)'=='$Configuration|$Platform'"
$outputPath = $null
foreach ($group in @($xml.Project.PropertyGroup)) {
    $cond = $group.GetAttribute('Condition')
    if ($cond -and (($cond -replace '\s', '') -ne $want)) { continue }
    foreach ($el in @($group.ChildNodes | Where-Object { $_.LocalName -eq 'OutputPath' })) {
        if ($el.GetAttribute('Condition')) { throw "OutputPath in $proj has a Condition. This script cannot evaluate it." }
        $outputPath = $el.InnerText   # MSBuild keeps the last assignment.
    }
}
if (-not $outputPath) { throw "No OutputPath for '$Configuration|$Platform' in $proj." }
if ($outputPath -match '\$\(') { throw "OutputPath '$outputPath' in $proj uses a property. This script reads literal paths only." }
$outDir = [IO.Path]::GetFullPath((Join-Path (Split-Path $proj) $outputPath))
$lockDirs = @($outDir)
$testOutDir = [IO.Path]::GetFullPath((Join-Path $repo "TcAutomation.Tests\bin\$Configuration"))
if ($Tests) { $lockDirs += $testOutDir }

# 3. Preflight before the interop step. A process started from an output folder
#    locks its assemblies. The copy then fails with MSB3021 after ten retries,
#    without naming the process. Best effort: paths are compared as strings, so a
#    junction to the same folder is missed, and a later process still locks it.
$blockers = @()
foreach ($proc in @(Get-Process TcAutomation, TcAutomation.Tests -ErrorAction SilentlyContinue)) {
    $procPath = $null
    try { $procPath = $proc.Path } catch { }
    if (-not $procPath) {
        Write-Warning "Cannot read the path of $($proc.ProcessName) PID $($proc.Id). If it runs from this build's output, the copy step fails with MSB3021."
        continue
    }
    $procDir = $null
    try { $procDir = [IO.Path]::GetFullPath((Split-Path $procPath -Parent)).TrimEnd('\') } catch { }
    if ($procDir -and ($lockDirs | Where-Object { $_.TrimEnd('\') -ieq $procDir })) {
        $blockers += "    PID $($proc.Id)  $procPath"
    }
}
if ($blockers.Count -gt 0) {
    throw @"
A process is running from this build's output folder:
$($blockers -join "`n")

Stop it, then re-run. For a TcAutomation.exe worker, stop the MCP client that
owns it. A command may be mid-execution, so reconcile active work first. Killing
the worker does not establish what an interrupted command did to the target.
"@
}

# 4. Regenerate the TCatSysManagerLib interop every time, so a TwinCAT upgrade
#    cannot leave a stale one. TypeLibConverter needs Windows PowerShell 5.1.
$interop = Join-Path $repo 'TcAutomation\interop\Interop.TCatSysManagerLib.dll'
Write-Host "Generating COM interop..." -ForegroundColor Yellow
$ps51 = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
if (-not (Test-Path $ps51)) { $ps51 = Join-Path $env:SystemRoot 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe' }
if (-not (Test-Path $ps51)) { throw "Windows PowerShell 5.1 (powershell.exe) not found. The interop step needs it." }
# Windows PowerShell keeps its own execution policy, separate from PowerShell 7.
& $ps51 -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'TcAutomation\gen-interop.ps1')
if ($LASTEXITCODE -ne 0) { throw "Interop generation failed ($LASTEXITCODE)." }
if (-not (Test-Path $interop)) { throw "Interop generation failed: $interop not produced." }

# 5. Restore and build. DiscoverEditorConfigFiles=false repeats the props value
#    as a global property, so a csproj override cannot bring back MSB4184.
$common = @(
    "/p:NoVisualStudioBuild=true",
    "/p:DiscoverEditorConfigFiles=false",
    "/p:Configuration=$Configuration",
    "/p:Platform=$Platform",
    "/v:minimal"
)
$target = if ($Tests) { $testProj } else { $proj }
& $msbuild $target /t:Restore /p:RestoreForce=true @common
if ($LASTEXITCODE -ne 0) { throw "Restore failed ($LASTEXITCODE)." }
& $msbuild $target @common
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)." }

$exe = Join-Path $outDir 'TcAutomation.exe'
if (-not (Test-Path $exe)) { throw "Build reported success but $exe is missing." }
Write-Host "`n[OK] Built $exe" -ForegroundColor Green
if ($Tests) {
    $testExe = Join-Path $testOutDir 'TcAutomation.Tests.exe'
    if (-not (Test-Path $testExe)) { throw "Build reported success but $testExe is missing." }
    Write-Host "[OK] Built $testExe" -ForegroundColor Green
}
