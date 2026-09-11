param([Parameter(Mandatory = $true)][string]$AssemblyPath)
$ErrorActionPreference = 'Stop'
# Tests launch preparation and PID isolation without starting XAE or contacting a PLC.
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $AssemblyPath).Path)
$type = $assembly.GetType('TcAutomation.Core.VisualStudioInstance', $true)
$flags = [Reflection.BindingFlags]'Static,NonPublic'
$match = $type.GetMethod('IsOwnedDteMoniker', $flags)
$prepare = $type.GetMethod('CreateDevelopmentToolsStartInfo', $flags)
$cases = @(
    @('!TcXaeShell.DTE.17.0:1234', $true),
    @('!VisualStudio.DTE.17.0:1234', $true),
    @('TcXaeShell.DTE.17.0:1234', $true),
    @('!TcXaeShell.DTE.17.0:12345', $false),
    @('!TcXaeShell.DTE.17.0:123', $false),
    @('!TcXaeShell.DTE.15.0:1234', $false),
    @('!Other.DTE.17.0:1234', $false)
)
foreach ($case in $cases) {
    $actual = $match.Invoke($null, [object[]]@([string]$case[0], 'TcXaeShell.DTE.17.0', [int]1234))
    if ($actual -ne $case[1]) { throw "Wrong PID/moniker decision for $($case[0])" }
}
$originalPath = $env:PATH
$executable = Join-Path $PSHOME 'powershell.exe'
foreach ($command in @($executable, ('"' + $executable + '" /Embedding'))) {
    $start = $prepare.Invoke($null, [object[]]@([string]$command))
    if ($start.FileName -ne $executable) { throw 'Registered executable path was not preserved.' }
    if ($start.UseShellExecute -or -not $start.CreateNoWindow -or $start.WindowStyle -ne 'Hidden') {
        throw 'Launch must use a hidden child process with explicit environment.'
    }
    if ($command.StartsWith('"') -and $start.Arguments -ne '/Embedding') { throw 'Registered arguments lost.' }
    if (-not $command.StartsWith('"') -and $start.Arguments -ne '-Embedding') { throw 'Automation-server startup mode missing.' }
    $common64 = Join-Path ${env:ProgramFiles(x86)} 'Beckhoff\TwinCAT\Common64'
    if ((Test-Path -LiteralPath $common64) -and -not $start.EnvironmentVariables['PATH'].StartsWith($common64 + ';')) {
        throw 'Child native-library path missing Common64 priority.'
    }
}
if ($env:PATH -ne $originalPath) { throw 'Launch preparation mutated the caller environment.' }
$rejected = $false
try { $null = $prepare.Invoke($null, [object[]]@('relative.exe')) } catch { $rejected = $true }
if (-not $rejected) { throw 'Relative server executable was accepted.' }
Write-Output 'PASS: 10 cases (7 PID/moniker cases, 2 launch-preparation cases, 1 invalid-path case), plus caller-environment preservation. No XAE launched.'
