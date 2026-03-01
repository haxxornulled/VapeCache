param(
    [int]$Connections = 4,
    [int]$MaxInFlight = 2048,
    [int]$Workers = 48,
    [int]$DurationSeconds = 12,
    [int]$KillRounds = 6,
    [int]$KillIntervalMs = 800,
    [switch]$DisableCoalescing,
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

function Get-EnvValue([string]$name) {
    $value = [Environment]::GetEnvironmentVariable($name)
    if (-not [string]::IsNullOrWhiteSpace($value)) {
        return $value
    }

    return [Environment]::GetEnvironmentVariable($name, [EnvironmentVariableTarget]::User)
}

$resolvedConnectionString = Get-EnvValue "VAPECACHE_REDIS_CONNECTIONSTRING"
$resolvedHost = Get-EnvValue "VAPECACHE_REDIS_HOST"

if ([string]::IsNullOrWhiteSpace($resolvedConnectionString) -and
    [string]::IsNullOrWhiteSpace($resolvedHost)) {
    Write-Host "Set VAPECACHE_REDIS_CONNECTIONSTRING or VAPECACHE_REDIS_HOST before running reconnect drill."
    exit 1
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$testProject = Join-Path $repoRoot "VapeCache.Tests\VapeCache.Tests.csproj"

$env:VAPECACHE_RECONNECT_DRILL_ENABLED = "true"
$env:VAPECACHE_RECONNECT_DRILL_CONNECTIONS = "$Connections"
$env:VAPECACHE_RECONNECT_DRILL_MAX_INFLIGHT = "$MaxInFlight"
$env:VAPECACHE_RECONNECT_DRILL_WORKERS = "$Workers"
$env:VAPECACHE_RECONNECT_DRILL_DURATION_SECONDS = "$DurationSeconds"
$env:VAPECACHE_RECONNECT_DRILL_KILL_ROUNDS = "$KillRounds"
$env:VAPECACHE_RECONNECT_DRILL_KILL_INTERVAL_MS = "$KillIntervalMs"
$env:VAPECACHE_RECONNECT_DRILL_COALESCE = if ($DisableCoalescing.IsPresent) { "false" } else { "true" }

if (-not [string]::IsNullOrWhiteSpace($resolvedConnectionString)) {
    $env:VAPECACHE_REDIS_CONNECTIONSTRING = $resolvedConnectionString
}
if (-not [string]::IsNullOrWhiteSpace($resolvedHost)) {
    $env:VAPECACHE_REDIS_HOST = $resolvedHost
}

Write-Host "Running Redis reconnect drill against live endpoint..."
Write-Host " Connections=$Connections MaxInFlight=$MaxInFlight Workers=$Workers CoalescedWrites=$(-not $DisableCoalescing.IsPresent)"
Write-Host " DurationSeconds=$DurationSeconds KillRounds=$KillRounds KillIntervalMs=$KillIntervalMs"
Write-Host " Safety: CLIENT KILL only targets mux lane clients tagged by this drill run."

dotnet test $testProject `
    --configuration $Configuration `
    --nologo `
    --filter "FullyQualifiedName~VapeCache.Tests.Integration.RedisReconnectDrillIntegrationTests.ForcedClientKill_ReconnectsAndSustainsTraffic"
