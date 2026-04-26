param(
    [ValidateSet("smoke", "soak")]
    [string]$Mode = "smoke",
    [Alias("Host")]
    [string]$RedisHost = "localhost",
    [int]$Port = 6379,
    [string]$Username = "",
    [string]$Password = "",
    [switch]$UseTls,
    [int]$Workers = 24,
    [int]$Keyspace = 256,
    [int]$DurationSeconds = 10,
    [int]$Connections = 8,
    [int]$MaxInFlight = 4096,
    [int]$ForceOpenAfterMs = 1500,
    [int]$ForceOpenHoldMs = 2500,
    [int]$ForceOpenCycles = 3,
    [switch]$IncludeReconnectDrill,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$testProject = Join-Path $repoRoot "VapeCache.Tests\VapeCache.Tests.csproj"

if ($Mode -eq "soak") {
    if (-not $PSBoundParameters.ContainsKey("Workers")) { $Workers = 32 }
    if (-not $PSBoundParameters.ContainsKey("Keyspace")) { $Keyspace = 256 }
    if (-not $PSBoundParameters.ContainsKey("DurationSeconds")) { $DurationSeconds = 20 }
    if (-not $PSBoundParameters.ContainsKey("Connections")) { $Connections = 8 }
    if (-not $PSBoundParameters.ContainsKey("ForceOpenAfterMs")) { $ForceOpenAfterMs = 1200 }
    if (-not $PSBoundParameters.ContainsKey("ForceOpenHoldMs")) { $ForceOpenHoldMs = 1800 }
    if (-not $PSBoundParameters.ContainsKey("ForceOpenCycles")) { $ForceOpenCycles = 3 }
}

$env:VAPECACHE_REDIS_HOST = $RedisHost
$env:VAPECACHE_REDIS_PORT = $Port.ToString()
$env:VAPECACHE_RUNTIME_STRESS_ENABLED = "true"
$env:VAPECACHE_RUNTIME_STRESS_WORKERS = $Workers.ToString()
$env:VAPECACHE_RUNTIME_STRESS_KEYSPACE = $Keyspace.ToString()
$env:VAPECACHE_RUNTIME_STRESS_DURATION_SECONDS = $DurationSeconds.ToString()
$env:VAPECACHE_RUNTIME_STRESS_CONNECTIONS = $Connections.ToString()
$env:VAPECACHE_RUNTIME_STRESS_MAX_INFLIGHT = $MaxInFlight.ToString()
$env:VAPECACHE_RUNTIME_STRESS_FORCE_OPEN_AFTER_MS = $ForceOpenAfterMs.ToString()
$env:VAPECACHE_RUNTIME_STRESS_FORCE_OPEN_HOLD_MS = $ForceOpenHoldMs.ToString()
$env:VAPECACHE_RUNTIME_STRESS_FORCE_OPEN_CYCLES = $ForceOpenCycles.ToString()

if (-not [string]::IsNullOrWhiteSpace($Username)) {
    $env:VAPECACHE_REDIS_USERNAME = $Username
}
else {
    Remove-Item Env:VAPECACHE_REDIS_USERNAME -ErrorAction SilentlyContinue
}

if (-not [string]::IsNullOrWhiteSpace($Password)) {
    $env:VAPECACHE_REDIS_PASSWORD = $Password
}
else {
    Remove-Item Env:VAPECACHE_REDIS_PASSWORD -ErrorAction SilentlyContinue
}

if ($UseTls) {
    $env:VAPECACHE_REDIS_USE_TLS = "true"
}
else {
    Remove-Item Env:VAPECACHE_REDIS_USE_TLS -ErrorAction SilentlyContinue
}

$filter = if ($Mode -eq "soak") {
    "FullyQualifiedName~VapeCache.Tests.Integration.RuntimeStressIntegrationTests.RepeatedFailoverCycles_DuringMixedTraffic_StayAvailable_AndKeepTelemetryHonest"
}
else {
    "FullyQualifiedName~VapeCache.Tests.Integration.RuntimeStressIntegrationTests"
}

if ($IncludeReconnectDrill.IsPresent) {
    $env:VAPECACHE_RECONNECT_DRILL_ENABLED = "true"
    $env:VAPECACHE_RECONNECT_DRILL_CONNECTIONS = [Math]::Max(4, $Connections).ToString()
    $env:VAPECACHE_RECONNECT_DRILL_MAX_INFLIGHT = $MaxInFlight.ToString()
    $env:VAPECACHE_RECONNECT_DRILL_WORKERS = [Math]::Max(24, $Workers).ToString()
    $env:VAPECACHE_RECONNECT_DRILL_DURATION_SECONDS = [Math]::Max(12, $DurationSeconds).ToString()
    $env:VAPECACHE_RECONNECT_DRILL_KILL_ROUNDS = "6"
    $env:VAPECACHE_RECONNECT_DRILL_KILL_INTERVAL_MS = "800"
    $env:VAPECACHE_RECONNECT_DRILL_COALESCE = "true"
    $filter = "$filter|FullyQualifiedName~VapeCache.Tests.Integration.RedisReconnectDrillIntegrationTests"
}

Write-Host "Running VapeCache runtime stress suite against $RedisHost`:$Port"
Write-Host " Mode=$Mode Workers=$Workers Keyspace=$Keyspace DurationSeconds=$DurationSeconds Connections=$Connections MaxInFlight=$MaxInFlight"
Write-Host " ForceOpenAfterMs=$ForceOpenAfterMs ForceOpenHoldMs=$ForceOpenHoldMs ForceOpenCycles=$ForceOpenCycles ReconnectDrill=$($IncludeReconnectDrill.IsPresent)"

dotnet test $testProject `
    --configuration $Configuration `
    --nologo `
    --filter $filter `
    --verbosity normal
