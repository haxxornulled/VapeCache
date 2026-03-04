param(
    [string]$Job = "Short",
    [string]$Mode = "fair",
    [ValidateSet("standard", "aggressive", "extreme")]
    [string]$Profile = "aggressive",
    [string]$ContentionProcessorCounts = "4,16,32",
    [double]$ClientMaxRatio = 1.00
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$artifactsRoot = Join-Path $repoRoot "BenchmarkDotNet.Artifacts/head-to-head/contention-gate/$timestamp"

Write-Host "Running contention benchmark matrix for perf-gate..."
pwsh -File (Join-Path $PSScriptRoot "run-head-to-head-benchmarks.ps1") `
    -Suite client `
    -Job $Job `
    -Mode $Mode `
    -Profile $Profile `
    -ArtifactsRoot $artifactsRoot `
    -ContentionMatrix `
    -ContentionProcessorCounts $ContentionProcessorCounts | Out-Host

$cpuDirs = Get-ChildItem -Path $artifactsRoot -Directory -Filter "cpu-*" -ErrorAction SilentlyContinue
if (($cpuDirs | Measure-Object).Count -eq 0) {
    Write-Error "No contention profile directories were produced under '$artifactsRoot'."
    exit 1
}

foreach ($cpuDir in $cpuDirs) {
    $comparison = Get-ChildItem -Path $cpuDir.FullName -Filter comparison.md -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1

    if ($null -eq $comparison) {
        Write-Error "No comparison.md found for profile '$($cpuDir.Name)'."
        exit 1
    }

    Write-Host "Validating ratio gate for $($cpuDir.Name): $($comparison.FullName)"
    pwsh -File (Join-Path $PSScriptRoot "perf-gate.ps1") `
        -EnforceBenchmarkRatios `
        -ComparisonPath $comparison.FullName `
        -ClientMaxRatio $ClientMaxRatio `
        -EndToEndMaxRatio 999 `
        -ModulesMaxRatio 999 | Out-Host
}

Write-Host "Contention perf-gate completed successfully. Artifacts: $artifactsRoot"
