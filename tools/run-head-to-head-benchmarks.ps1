param(
    [string]$Job = "Short",
    [string]$ConnectionString = ""
)

$ErrorActionPreference = "Stop"

if (-not [string]::IsNullOrWhiteSpace($ConnectionString)) {
    $env:VAPECACHE_REDIS_CONNECTIONSTRING = $ConnectionString
}

if ([string]::IsNullOrWhiteSpace($env:VAPECACHE_REDIS_CONNECTIONSTRING) -and [string]::IsNullOrWhiteSpace($env:VAPECACHE_REDIS_HOST)) {
    Write-Host "Set VAPECACHE_REDIS_CONNECTIONSTRING or VAPECACHE_REDIS_HOST before running head-to-head benchmarks."
    exit 1
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $repoRoot "VapeCache.Benchmarks\VapeCache.Benchmarks.csproj"
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$artifacts = Join-Path $repoRoot "BenchmarkDotNet.Artifacts\head-to-head\$timestamp"

Write-Host "Running head-to-head benchmarks..."
Write-Host "Artifacts: $artifacts"

dotnet run -c Release --project $project -- `
    -j $Job `
    --filter "*RedisClientHeadToHeadBenchmarks*" "*RedisEndToEndHeadToHeadBenchmarks*" "*RedisModuleHeadToHeadBenchmarks*" `
    --artifacts $artifacts

$comparisonFiles = Get-ChildItem -Path $artifacts -Filter comparison.md -Recurse -ErrorAction SilentlyContinue
if ($comparisonFiles.Count -eq 0) {
    Write-Host "No comparison.md generated."
    exit 1
}

Write-Host ""
Write-Host "Comparison reports:"
$comparisonFiles | ForEach-Object { Write-Host " - $($_.FullName)" }
