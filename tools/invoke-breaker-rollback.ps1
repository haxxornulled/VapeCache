param(
    [ValidateSet("force-open", "clear")]
    [string]$Action = "force-open",
    [Alias("BaseUrl")]
    [string]$AdminBaseUrl = "http://localhost:5000",
    [string]$Prefix = "/vapecache",
    [string]$Reason = "manual-rollback"
)

$ErrorActionPreference = "Stop"

function Join-Url([string]$baseUrl, [string]$path)
{
    $left = $baseUrl.TrimEnd('/')
    $right = $path
    if (-not $right.StartsWith("/")) { $right = "/" + $right }
    return "$left$right"
}

$normalizedPrefix = if ($Prefix.StartsWith("/")) { $Prefix } else { "/$Prefix" }

if ($Action -eq "force-open")
{
    $uri = Join-Url $AdminBaseUrl "$normalizedPrefix/breaker/force-open"
    $payload = @{ reason = $Reason } | ConvertTo-Json -Compress
    $response = Invoke-RestMethod -Method Post -Uri $uri -ContentType "application/json" -Body $payload -TimeoutSec 10
    Write-Host "Rollback switch enabled (forced memory fallback)."
    Write-Host (" IsForcedOpen={0} Reason={1}" -f $response.IsForcedOpen, $response.Reason)
    exit 0
}

$clearUri = Join-Url $AdminBaseUrl "$normalizedPrefix/breaker/clear"
$clearResponse = Invoke-RestMethod -Method Post -Uri $clearUri -TimeoutSec 10
Write-Host "Rollback switch cleared (Redis traffic re-enabled)."
Write-Host (" IsForcedOpen={0} Reason={1}" -f $clearResponse.IsForcedOpen, $clearResponse.Reason)
exit 0
