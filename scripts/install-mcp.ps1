# Register the v2 server using its pinned local Python environment.
param([switch]$Insiders, [switch]$Workspace, [string]$InstallPath)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path "$PSScriptRoot/..").Path
$serverPath = if ($InstallPath) { (Resolve-Path -LiteralPath $InstallPath).Path } else { Join-Path $root "mcp-server/server.py" }
$pythonPath = Join-Path $root ".venv/Scripts/python.exe"
if (-not (Test-Path -LiteralPath $pythonPath)) { throw "Run scripts/setup.ps1 first." }
& $pythonPath -m pip install -r "$root/mcp-server/requirements.txt"
if ($LASTEXITCODE -ne 0) { throw "Could not install pinned server dependencies." }
$entry = @{ type = "stdio"; command = ($pythonPath -replace '\\','/'); args = @($serverPath -replace '\\','/') }
if ($Workspace) {
    $directory = Join-Path (Get-Location) ".vscode"
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $path = Join-Path $directory "mcp.json"
    # Preserve other registered servers and inputs.
    $config = if (Test-Path -LiteralPath $path) { Get-Content -LiteralPath $path -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
    if (-not $config.PSObject.Properties['servers']) { $config | Add-Member -NotePropertyName servers -NotePropertyValue ([pscustomobject]@{}) }
    $config.servers | Add-Member -NotePropertyName "twincat-automation" -NotePropertyValue $entry -Force
    $config | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath $path -Encoding utf8
    Write-Host "Registered TwinCAT MCP in $path"
} else {
    $command = if ($Insiders) { "code-insiders" } else { "code" }
    $cli = Get-Command $command -ErrorAction Stop
    $entry.name = "twincat-automation"
    $payload = $entry | ConvertTo-Json -Compress
    & $cli.Source --add-mcp $payload
    if ($LASTEXITCODE -ne 0) { throw "VS Code registration failed." }
    Write-Host "Registered TwinCAT MCP. Reload the MCP server to load the three-tool interface."
}
