param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [ValidateSet("smoke", "full")]
    [string]$Mode = "smoke",
    [ValidateSet("redis", "keydb", "both")]
    [string]$Backend = "both",
    [string]$Filter = "*RedisBackendRoundTripBenchmarks*",
    [string]$Artifacts = "",
    [switch]$EnsureContainers
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$composeFile = Join-Path $repoRoot "infra\docker-compose.benchmarks.yml"
$exporterScript = Join-Path $PSScriptRoot "export-live-benchmark-comparison.ps1"

function Get-ArtifactsRoot {
    param([string]$ArtifactsPath)

    if (-not [string]::IsNullOrWhiteSpace($ArtifactsPath)) {
        return (Resolve-Path $ArtifactsPath).Path
    }

    return (Join-Path $repoRoot "BenchmarkDotNet.Artifacts")
}

if ($EnsureContainers) {
    & docker compose -f $composeFile up -d
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to start benchmark containers."
    }
}

$env:VAPECACHE_BENCH_REDIS_HOST = "127.0.0.1"
$env:VAPECACHE_BENCH_REDIS_PORT = "6379"
$env:VAPECACHE_BENCH_KEYDB_HOST = "127.0.0.1"
$env:VAPECACHE_BENCH_KEYDB_PORT = "6380"
$env:VAPECACHE_BENCH_BACKENDS = if ($Backend -eq "both") { "redis,keydb" } else { $Backend }
$env:VAPECACHE_BENCH_RUN_PROFILE = $Mode
$env:VAPECACHE_BENCH_PAYLOADS = if ($Mode -eq "smoke") { "256" } else { "256,1024" }

$extraArgs = @()
$extraArgs += "--include-live"
$extraArgs += @("--filter", $Filter)

if (-not [string]::IsNullOrWhiteSpace($Artifacts)) {
    $extraArgs += @("--artifacts", $Artifacts)
}

& pwsh -File (Join-Path $PSScriptRoot "run-benchmarks.ps1") -Configuration $Configuration @extraArgs
if ($LASTEXITCODE -ne 0) {
    throw "Live benchmark run failed with exit code $LASTEXITCODE."
}

$artifactsRoot = Get-ArtifactsRoot -ArtifactsPath $Artifacts

if ($Backend -eq "both") {
    & pwsh -File $exporterScript -ArtifactsRoot $artifactsRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Live benchmark comparison export failed with exit code $LASTEXITCODE."
    }
}
