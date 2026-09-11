# Package the classic .NET Framework worker and Python source.
$ErrorActionPreference = "Stop"
$root = (Resolve-Path "$PSScriptRoot/..").Path
& "$PSScriptRoot/build.ps1"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$destination = Join-Path $root "TcAutomation/publish"
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Copy-Item -Path "$root/TcAutomation/bin/Release-v2/*" -Destination $destination -Force
Write-Host "Worker copied to $destination. Distribute mcp-server with requirements.txt and operations.json alongside the repository layout."
