param(
    [switch]$EnforceBenchmarkRatios,
    [string]$ComparisonPath = "",
    [double]$ClientMaxRatio = 1.00,
    [double]$EndToEndMaxRatio = 1.00,
    [double]$ModulesMaxRatio = 1.00
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$violations = New-Object System.Collections.Generic.List[string]

$hotPathSignatureFiles = @(
    "VapeCache.Abstractions/Connections/IRedisCommandExecutor.cs",
    "VapeCache.Abstractions/Caching/ICacheService.cs",
    "VapeCache.Console/GroceryStore/StackExchangeRedisGroceryStoreService.cs"
)

foreach ($relativePath in $hotPathSignatureFiles) {
    $fullPath = Join-Path $repoRoot $relativePath
    if (-not (Test-Path $fullPath)) {
        $violations.Add("Missing expected hot-path file: $relativePath")
        continue
    }

    $content = Get-Content $fullPath -Raw
    $badMatches = [regex]::Matches($content, "(?<!Value)Task<|(?<!Value)Task\b")
    if ($badMatches.Count -gt 0) {
        $violations.Add("$relativePath contains Task-based signatures. Use ValueTask for hot-path APIs.")
    }
}

if ($EnforceBenchmarkRatios.IsPresent) {
    if (-not [string]::IsNullOrWhiteSpace($ComparisonPath)) {
        if (-not [System.IO.Path]::IsPathRooted($ComparisonPath)) {
            $ComparisonPath = Join-Path $repoRoot $ComparisonPath
        }
    }
    else {
        $candidates = @(
            Join-Path $repoRoot "BenchmarkDotNet.Artifacts/head-to-head",
            Join-Path $repoRoot "BenchmarkDotNet.Artifacts/results",
            Join-Path $repoRoot "BenchmarkDotNet.Artifacts"
        )

        $latest = $candidates |
            Where-Object { Test-Path $_ } |
            ForEach-Object { Get-ChildItem -Path $_ -Filter comparison.md -Recurse -ErrorAction SilentlyContinue } |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1

        if ($null -ne $latest) {
            $ComparisonPath = $latest.FullName
        }
    }

    if ([string]::IsNullOrWhiteSpace($ComparisonPath) -or -not (Test-Path $ComparisonPath)) {
        $violations.Add("Benchmark ratio gate enabled, but no comparison.md was found. Pass -ComparisonPath or run head-to-head benchmarks first.")
    }
    else {
        $thresholdBySuite = @{
            "RedisClientHeadToHeadBenchmarks" = $ClientMaxRatio
            "RedisEndToEndHeadToHeadBenchmarks" = $EndToEndMaxRatio
            "RedisModuleHeadToHeadBenchmarks" = $ModulesMaxRatio
        }
        $seenSuites = New-Object 'System.Collections.Generic.HashSet[string]'
        $rawLines = Get-Content $ComparisonPath
        $linePattern = '^\|(?<suite>[^|]+)\|(?<scenario>[^|]+)\|(?<params>[^|]*)\|(?<ser>[^|]+)\|(?<vape>[^|]+)\|(?<ratio>[^|]+)\|'

        foreach ($line in $rawLines) {
            $match = [regex]::Match($line, $linePattern)
            if (-not $match.Success) {
                continue
            }

            $suite = $match.Groups["suite"].Value.Trim()
            $scenario = $match.Groups["scenario"].Value.Trim()
            if (-not $thresholdBySuite.ContainsKey($suite)) {
                continue
            }

            $seenSuites.Add($suite) | Out-Null

            $ratioText = $match.Groups["ratio"].Value.Trim()
            $ratio = 0.0
            $isNumeric = [double]::TryParse(
                $ratioText,
                [System.Globalization.NumberStyles]::Float,
                [System.Globalization.CultureInfo]::InvariantCulture,
                [ref]$ratio)

            if (-not $isNumeric) {
                continue
            }

            $threshold = [double]$thresholdBySuite[$suite]
            if ($ratio -gt $threshold) {
                $violations.Add("Benchmark ratio regression in ${suite}/${scenario}: Vape/SER ratio $ratioText exceeded threshold $threshold (source: $ComparisonPath).")
            }
        }

        foreach ($suite in $thresholdBySuite.Keys) {
            if (-not $seenSuites.Contains($suite)) {
                $violations.Add("Benchmark ratio gate enabled but suite '$suite' was not found in $ComparisonPath.")
            }
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Error "Perf gate failed:`n$($violations -join "`n")"
    exit 1
}

Write-Host "Perf gates: async API conventions validated for hot-path signatures."
Write-Host "Perf gates: CI should also run VapeCache.PerfGates.Tests for allocation assertions."
if ($EnforceBenchmarkRatios.IsPresent) {
    Write-Host "Perf gates: benchmark ratio thresholds validated using '$ComparisonPath'."
}
else {
    Write-Host "Perf gates: benchmark ratio checks skipped (enable with -EnforceBenchmarkRatios)."
}
exit 0
