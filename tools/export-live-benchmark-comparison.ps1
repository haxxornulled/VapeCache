param(
    [string]$ArtifactsRoot = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
if ([string]::IsNullOrWhiteSpace($ArtifactsRoot)) {
    $ArtifactsRoot = Join-Path $repoRoot "BenchmarkDotNet.Artifacts"
}

$resultsDir = Join-Path $ArtifactsRoot "results"
$csvPath = Join-Path $resultsDir "VapeCache.Benchmarks.Benchmarks.RedisBackendRoundTripBenchmarks-report.csv"
$outputPath = Join-Path $resultsDir "RedisVsKeyDbComparison.md"

if (-not (Test-Path $csvPath)) {
    throw "Live benchmark csv not found: $csvPath"
}

function Convert-ToMicroseconds {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    if ($Value -match '^\s*([0-9]+(?:\.[0-9]+)?)\s*(ns|μs|us|ms|s)\s*$') {
        $number = [double]$Matches[1]
        $unit = $Matches[2]

        switch ($unit) {
            'ns' { return $number / 1000.0 }
            'μs' { return $number }
            'us' { return $number }
            'ms' { return $number * 1000.0 }
            's' { return $number * 1000000.0 }
        }
    }

    throw "Unsupported duration value '$Value'"
}

function Convert-ToBytes {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    if ($Value -match '^\s*([0-9]+(?:\.[0-9]+)?)\s*(B|KB|MB|GB)\s*$') {
        $number = [double]$Matches[1]
        $unit = $Matches[2]

        switch ($unit) {
            'B' { return $number }
            'KB' { return $number * 1024.0 }
            'MB' { return $number * 1024.0 * 1024.0 }
            'GB' { return $number * 1024.0 * 1024.0 * 1024.0 }
        }
    }

    throw "Unsupported allocation value '$Value'"
}

function Format-Delta {
    param(
        [double]$Current,
        [double]$Reference,
        [string]$Unit
    )

    $delta = $Current - $Reference
    $sign = if ($delta -gt 0) { '+' } elseif ($delta -lt 0) { '-' } else { '' }
    return "{0}{1:N1} {2}" -f $sign, [Math]::Abs($delta), $Unit
}

$rows = Import-Csv -Path $csvPath
$pairs = $rows | Group-Object Method, PayloadBytes, Job

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('# Redis vs KeyDB Comparison')
$lines.Add('')
$lines.Add("Generated from $([System.IO.Path]::GetFileName($csvPath)).")
$lines.Add('')
$lines.Add('| Method | PayloadBytes | Job | Redis Mean | KeyDB Mean | Faster Backend | Latency Delta | Redis Allocated | KeyDB Allocated | Allocation Delta |')
$lines.Add('|---|---:|---|---:|---:|---|---:|---:|---:|---:|')

foreach ($pair in $pairs) {
    $redis = $pair.Group | Where-Object Backend -eq 'redis' | Select-Object -First 1
    $keydb = $pair.Group | Where-Object Backend -eq 'keydb' | Select-Object -First 1

    if ($null -eq $redis -or $null -eq $keydb) {
        continue
    }

    $redisMeanUs = Convert-ToMicroseconds $redis.Mean
    $keydbMeanUs = Convert-ToMicroseconds $keydb.Mean
    $redisAllocatedBytes = Convert-ToBytes $redis.Allocated
    $keydbAllocatedBytes = Convert-ToBytes $keydb.Allocated

    $fasterBackend = if ($redisMeanUs -le $keydbMeanUs) { 'redis' } else { 'keydb' }
    $latencyDelta = Format-Delta -Current $keydbMeanUs -Reference $redisMeanUs -Unit 'us'
    $allocationDelta = if ($null -ne $redisAllocatedBytes -and $null -ne $keydbAllocatedBytes) {
        Format-Delta -Current $keydbAllocatedBytes -Reference $redisAllocatedBytes -Unit 'B'
    }
    else {
        'n/a'
    }

    $lines.Add("| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9} |" -f `
        $redis.Method,
        $redis.PayloadBytes,
        $redis.Job,
        $redis.Mean,
        $keydb.Mean,
        $fasterBackend,
        $latencyDelta,
        $redis.Allocated,
        $keydb.Allocated,
        $allocationDelta)
}

if ($lines.Count -eq 6) {
    throw 'No Redis/KeyDB benchmark pairs were found in the live benchmark csv.'
}

[System.IO.File]::WriteAllLines($outputPath, $lines)
Write-Host "Wrote comparison report to $outputPath"