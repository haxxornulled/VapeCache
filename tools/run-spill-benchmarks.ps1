param(
    [ValidateSet("Dry", "Short", "Medium", "Long")]
    [string]$Job = "Short",
    [switch]$Quick,
    [string]$Payloads = "",
    [string]$WorkingSet = "",
    [string]$SegmentMegabytes = "",
    [string]$ArtifactsRoot = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$runnerProject = Join-Path $repoRoot "VapeCache.Benchmarks\VapeCache.Benchmarks.Runner.csproj"

if ($Quick.IsPresent) {
    $env:VAPECACHE_BENCH_QUICK = "true"
    if ($Job -eq "Short") {
        $Job = "Dry"
    }
}
else {
    $env:VAPECACHE_BENCH_QUICK = "false"
}

if (-not [string]::IsNullOrWhiteSpace($Payloads)) {
    $env:VAPECACHE_BENCH_SPILL_PAYLOADS = $Payloads
}
if (-not [string]::IsNullOrWhiteSpace($WorkingSet)) {
    $env:VAPECACHE_BENCH_SPILL_WORKING_SET = $WorkingSet
}
if (-not [string]::IsNullOrWhiteSpace($SegmentMegabytes)) {
    $env:VAPECACHE_BENCH_SPILL_SEGMENT_MB = $SegmentMegabytes
}

if ([string]::IsNullOrWhiteSpace($ArtifactsRoot)) {
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $ArtifactsRoot = Join-Path $repoRoot "BenchmarkDotNet.Artifacts\spill\$timestamp"
}

New-Item -ItemType Directory -Path $ArtifactsRoot -Force | Out-Null

Write-Host "Running spill benchmark suite..."
Write-Host "Project: $runnerProject"
Write-Host "Job: $Job"
Write-Host "Quick mode: $($Quick.IsPresent)"
Write-Host "Payloads: $env:VAPECACHE_BENCH_SPILL_PAYLOADS"
Write-Host "Working set: $env:VAPECACHE_BENCH_SPILL_WORKING_SET"
Write-Host "Segment MB: $env:VAPECACHE_BENCH_SPILL_SEGMENT_MB"
Write-Host "Artifacts: $ArtifactsRoot"

$arguments = @(
    "run"
    "-c"
    "Release"
    "--project"
    $runnerProject
    "--"
    "featuresets"
    "spill"
    "--job"
    $Job
    "--artifacts"
    $ArtifactsRoot
)

& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Spill benchmark run failed with exit code $LASTEXITCODE."
}

Write-Host ""
Write-Host "Spill benchmark run complete."
Write-Host "Artifacts: $ArtifactsRoot"
