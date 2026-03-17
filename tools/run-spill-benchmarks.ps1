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

function Get-PositiveIntList([string]$csv, [int[]]$defaults) {
    if ([string]::IsNullOrWhiteSpace($csv)) {
        return $defaults
    }

    $values = @()
    foreach ($part in $csv.Split(",", [System.StringSplitOptions]::RemoveEmptyEntries)) {
        $trimmed = $part.Trim()
        if ($trimmed -match "^\d+$") {
            $parsed = [int]$trimmed
            if ($parsed -gt 0) {
                $values += $parsed
            }
        }
    }

    if ($values.Count -eq 0) {
        return $defaults
    }

    return $values | Select-Object -Unique
}

$effectivePayloads = Get-PositiveIntList $env:VAPECACHE_BENCH_SPILL_PAYLOADS @(4096, 65536, 262144)
$effectiveWorkingSet = Get-PositiveIntList $env:VAPECACHE_BENCH_SPILL_WORKING_SET @(256, 1024)
$effectiveSegments = Get-PositiveIntList $env:VAPECACHE_BENCH_SPILL_SEGMENT_MB @(64, 128)

$methodCount = 7
$jobCount = 2
$caseCount = $effectivePayloads.Count * $effectiveWorkingSet.Count * $effectiveSegments.Count * $methodCount * $jobCount

$estimatedSetupBytes = 0L
foreach ($payload in $effectivePayloads) {
    foreach ($workingSetSize in $effectiveWorkingSet) {
        # Setup currently seeds read refs for both stores: 2 * workingSet writes.
        $setupBytesPerCase = 2L * $workingSetSize * $payload
        $estimatedSetupBytes += $setupBytesPerCase * $effectiveSegments.Count * $jobCount
    }
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
Write-Host ("Estimated benchmark cases: {0}" -f $caseCount)
Write-Host ("Estimated setup disk writes: {0:N2} GB" -f ($estimatedSetupBytes / 1GB))
if ($caseCount -ge 120 -or $estimatedSetupBytes -ge 8GB) {
    Write-Warning "Large benchmark matrix detected. Consider -Quick or narrower payload/working-set/segment values for faster turn-around."
}
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
