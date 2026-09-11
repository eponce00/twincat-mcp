# Protocol and application tests use fake workers; no PLC operations.
$ErrorActionPreference = "Stop"
$root = (Resolve-Path "$PSScriptRoot/..").Path
$pythonPath = Join-Path $root ".venv/Scripts/python.exe"
if (-not (Test-Path -LiteralPath $pythonPath)) { throw "Run scripts/setup.ps1 first." }
& $pythonPath -m unittest discover -s "$root/mcp-server/tests" -v
exit $LASTEXITCODE
