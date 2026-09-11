$ErrorActionPreference = "Stop"
$root = (Resolve-Path "$PSScriptRoot/..").Path
$pythonPath = Join-Path $root ".venv/Scripts/python.exe"
if (-not (Test-Path -LiteralPath $pythonPath)) { throw "Run setup.ps1 first." }
npx @modelcontextprotocol/inspector -- $pythonPath "$root/mcp-server/server.py"
exit $LASTEXITCODE
