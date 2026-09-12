param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Get", "Upsert", "Remove")]
    [string]$Action,

    [Parameter(Mandatory = $true)]
    [string]$PayloadPath
)

$ErrorActionPreference = "Stop"

function Convert-AdsRoute {
    param([Parameter(Mandatory = $true)]$Route)

    return [ordered]@{
        name = [string]$Route.Name
        amsNetId = [string]$Route.NetId
        address = [string]$Route.Address
        security = if (-not [bool]$Route.IsSecure) {
            "None"
        } elseif ($Route.FingerPrint) {
            "SelfSigned"
        } else {
            [string]$Route.Security
        }
        secure = [bool]$Route.IsSecure
        online = if ($null -eq $Route.IsOnlineNow) { $null } else { [bool]$Route.IsOnlineNow }
        fingerprint = [string]$Route.FingerPrint
    }
}

try {
    Import-Module TcXaeMgmt -ErrorAction Stop

    $payloadFile = (Resolve-Path -LiteralPath $PayloadPath -ErrorAction Stop).Path
    $payload = Get-Content -LiteralPath $payloadFile -Raw | ConvertFrom-Json

    switch ($Action) {
        "Get" {
            $routes = @(Get-AdsRoute -Force)

            if ($payload.amsNetId) {
                $routes = @($routes | Where-Object { [string]$_.NetId -eq [string]$payload.amsNetId })
            }

            $result = [ordered]@{
                success = $true
                routes = @($routes | ForEach-Object { Convert-AdsRoute $_ })
            }
        }

        "Upsert" {
            if (-not $payload.credentialPath) {
                throw "credentialPath is required for a self-signed Secure ADS route."
            }

            if (-not $payload.fingerprint) {
                throw "fingerprint is required so the target certificate is verified."
            }

            $credentialFile = (Resolve-Path -LiteralPath $payload.credentialPath -ErrorAction Stop).Path
            $credential = Import-Clixml -LiteralPath $credentialFile

            if ($credential -isnot [System.Management.Automation.PSCredential]) {
                throw "credentialPath must contain a DPAPI-protected PSCredential exported with Export-Clixml."
            }

            $route = Add-AdsRoute `
                -NetId ([string]$payload.amsNetId) `
                -IPOrHostName ([string]$payload.ipOrHostName) `
                -Name ([string]$payload.name) `
                -Credential $credential `
                -SelfSigned `
                -FingerPrint ([string]$payload.fingerprint) `
                -Unidirectional:([bool]$payload.unidirectional) `
                -Force `
                -Quiet `
                -PassThru

            $probe = Get-AdsState `
                -NetId ([string]$payload.amsNetId) `
                -Port ([int]$payload.verifyPort)

            $result = [ordered]@{
                success = [bool]$probe.Succeeded
                route = Convert-AdsRoute $route
                verification = [ordered]@{
                    port = [int]$payload.verifyPort
                    state = [string]$probe.State
                    latencyMs = [math]::Round($probe.Latency.TotalMilliseconds, 3)
                    succeeded = [bool]$probe.Succeeded
                    error = if ($null -eq $probe.Exception) { $null } else { [string]$probe.Exception.Message }
                }
            }
        }

        "Remove" {
            $removeArgs = @{
                NetId = [string]$payload.amsNetId
                Force = $true
                Quiet = $true
                Mode = if ([bool]$payload.removeRemote) { "Both" } else { "Single" }
            }

            if ([bool]$payload.removeRemote) {
                if (-not $payload.credentialPath) {
                    throw "credentialPath is required when removeRemote is true."
                }

                $credentialFile = (Resolve-Path -LiteralPath $payload.credentialPath -ErrorAction Stop).Path
                $removeArgs.Credentials = Import-Clixml -LiteralPath $credentialFile
            }

            Remove-AdsRoute @removeArgs

            $remaining = @(
                Get-AdsRoute -Force |
                    Where-Object { [string]$_.NetId -eq [string]$payload.amsNetId }
            )

            $result = [ordered]@{
                success = ($remaining.Count -eq 0)
                removed = ($remaining.Count -eq 0)
                amsNetId = [string]$payload.amsNetId
                removeRemote = [bool]$payload.removeRemote
            }
        }
    }
}
catch {
    $result = [ordered]@{
        success = $false
        errorMessage = $_.Exception.Message
    }
}

$result | ConvertTo-Json -Depth 8 -Compress

if (-not $result.success) {
    exit 1
}
