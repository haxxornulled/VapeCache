param(
    [string]$Job = "Short",
    [ValidateSet("all", "hotpath", "client", "throughput", "endtoend", "modules", "datatypes")]
    [string]$Suite = "all",
    [ValidateSet("fair", "realworld")]
    [string]$Mode = "fair",
    [ValidateSet("standard", "aggressive", "extreme")]
    [string]$Profile = "standard",
    [switch]$Quick,
    [switch]$TextPayload,
    [string]$ConnectionString = "",
    [string]$ArtifactsRoot = "",
    [switch]$AllReports,
    [switch]$ContentionMatrix,
    [string]$ContentionProcessorCounts = "4,16,32"
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

function Set-EnvIfEmpty([string]$name, [string]$value) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
        [Environment]::SetEnvironmentVariable($name, $value)
    }
}

switch ($Profile) {
    "aggressive" {
        Set-EnvIfEmpty "VAPECACHE_BENCH_LAUNCH_COUNT" "3"
        Set-EnvIfEmpty "VAPECACHE_BENCH_WARMUP_COUNT" "6"
        Set-EnvIfEmpty "VAPECACHE_BENCH_ITERATION_COUNT" "20"
        Set-EnvIfEmpty "VAPECACHE_BENCH_CLIENT_PAYLOADS" "256,1024,4096,16384,65536"
        Set-EnvIfEmpty "VAPECACHE_BENCH_THROUGHPUT_PAYLOADS" "256,2048,16384"
        Set-EnvIfEmpty "VAPECACHE_BENCH_THROUGHPUT_CONCURRENCY" "32,64,128,256"
        Set-EnvIfEmpty "VAPECACHE_BENCH_THROUGHPUT_PIPELINE_DEPTH" "8,16,32"
        Set-EnvIfEmpty "VAPECACHE_BENCH_THROUGHPUT_CONNECTIONS" "2,4,8,16"
        Set-EnvIfEmpty "VAPECACHE_BENCH_THROUGHPUT_TOTAL_OPS" "16384"
        Set-EnvIfEmpty "VAPECACHE_BENCH_E2E_PAYLOADS" "256,1024,4096,16384,65536"
        Set-EnvIfEmpty "VAPECACHE_BENCH_DATATYPE_PAYLOADS" "256,1024,4096,16384,65536"
        Set-EnvIfEmpty "VAPECACHE_BENCH_MODULE_JSON_CHARS" "256,1024,4096,16384"
    }
    "extreme" {
        Set-EnvIfEmpty "VAPECACHE_BENCH_LAUNCH_COUNT" "4"
        Set-EnvIfEmpty "VAPECACHE_BENCH_WARMUP_COUNT" "8"
        Set-EnvIfEmpty "VAPECACHE_BENCH_ITERATION_COUNT" "30"
        Set-EnvIfEmpty "VAPECACHE_BENCH_CLIENT_PAYLOADS" "256,1024,4096,16384,65536,262144"
        Set-EnvIfEmpty "VAPECACHE_BENCH_THROUGHPUT_PAYLOADS" "256,2048,16384,65536"
        Set-EnvIfEmpty "VAPECACHE_BENCH_THROUGHPUT_CONCURRENCY" "64,128,256,512"
        Set-EnvIfEmpty "VAPECACHE_BENCH_THROUGHPUT_PIPELINE_DEPTH" "16,32,64"
        Set-EnvIfEmpty "VAPECACHE_BENCH_THROUGHPUT_CONNECTIONS" "4,8,16,32"
        Set-EnvIfEmpty "VAPECACHE_BENCH_THROUGHPUT_TOTAL_OPS" "32768"
        Set-EnvIfEmpty "VAPECACHE_BENCH_E2E_PAYLOADS" "256,1024,4096,16384,65536"
        Set-EnvIfEmpty "VAPECACHE_BENCH_DATATYPE_PAYLOADS" "256,1024,4096,16384,65536"
        Set-EnvIfEmpty "VAPECACHE_BENCH_MODULE_JSON_CHARS" "256,1024,4096,16384,65536"
    }
}

if ($Quick.IsPresent) {
    $env:VAPECACHE_BENCH_QUICK = "true"
    if ($Job -eq "Short") {
        $Job = "Dry"
    }
}
else {
    $env:VAPECACHE_BENCH_QUICK = "false"
}

$env:VAPECACHE_BENCH_TEXT_PAYLOAD = if ($TextPayload.IsPresent) { "true" } else { "false" }
$env:VAPECACHE_BENCH_INSTRUMENT = if ($Mode -eq "realworld") { "true" } else { "false" }
$env:VAPECACHE_BENCH_DEDICATED_LANE_WORKERS = "true"
$env:VAPECACHE_BENCH_SOCKET_RESP_READER = "true"

switch ($Suite) {
    "hotpath" { $filters = @("*RedisClientHeadToHeadBenchmarks*", "*RedisThroughputHeadToHeadBenchmarks*", "*RedisEndToEndHeadToHeadBenchmarks*") }
    "client"   { $filters = @("*RedisClientHeadToHeadBenchmarks*") }
    "throughput" { $filters = @("*RedisThroughputHeadToHeadBenchmarks*") }
    "endtoend" { $filters = @("*RedisEndToEndHeadToHeadBenchmarks*") }
    "modules"  { $filters = @("*RedisModuleHeadToHeadBenchmarks*") }
    "datatypes" { $filters = @("*RedisDatatypeParityHeadToHeadBenchmarks*") }
    default    { $filters = @("*RedisClientHeadToHeadBenchmarks*", "*RedisThroughputHeadToHeadBenchmarks*", "*RedisEndToEndHeadToHeadBenchmarks*", "*RedisModuleHeadToHeadBenchmarks*", "*RedisDatatypeParityHeadToHeadBenchmarks*") }
}

$reportAudience = switch ($Suite) {
    "hotpath" { "hot-path comparison" }
    "client" { "hot-path comparison" }
    "throughput" { "hot-path comparison" }
    "endtoend" { "hot-path comparison" }
    "modules" { "extended parity comparison" }
    "datatypes" { "extended parity comparison" }
    default { "mixed comparison coverage" }
}
$env:VAPECACHE_BENCH_REPORT_AUDIENCE = $reportAudience

if ([string]::IsNullOrWhiteSpace($ArtifactsRoot)) {
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $artifacts = Join-Path $repoRoot "BenchmarkDotNet.Artifacts\head-to-head\$timestamp"
}
else {
    $artifacts = $ArtifactsRoot
}

New-Item -ItemType Directory -Path $artifacts -Force | Out-Null

Write-Host "Running head-to-head benchmarks..."
Write-Host "Artifacts: $artifacts"
Write-Host "Suite: $Suite"
Write-Host "Reporting audience: $reportAudience"
Write-Host "Job: $Job"
Write-Host "Mode: $Mode (VAPECACHE_BENCH_INSTRUMENT=$env:VAPECACHE_BENCH_INSTRUMENT)"
Write-Host "Profile: $Profile"
Write-Host "Quick mode: $($Quick.IsPresent)"
Write-Host "Text payload: $($TextPayload.IsPresent)"
Write-Host "Launch/Warmup/Iteration: $env:VAPECACHE_BENCH_LAUNCH_COUNT / $env:VAPECACHE_BENCH_WARMUP_COUNT / $env:VAPECACHE_BENCH_ITERATION_COUNT"

function Invoke-Benchmarks([string]$targetArtifacts) {
    $quotedProject = '"' + $project + '"'
    $quotedArtifacts = '"' + $targetArtifacts + '"'
    $argumentList = @(
        "run"
        "-c"
        "Release"
        "--project"
        $quotedProject
        "--"
        "-j"
        $Job
        "--filter"
    )
    $argumentList += $filters
    $argumentList += @(
        "--artifacts"
        $quotedArtifacts
    )

    $process = Start-Process -FilePath "dotnet" -ArgumentList $argumentList -NoNewWindow -PassThru
    $startedAt = Get-Date
    while (-not $process.WaitForExit(60000)) {
        $elapsedMinutes = [int][Math]::Floor(((Get-Date) - $startedAt).TotalMinutes)
        Write-Host "Benchmark run still executing ($elapsedMinutes minute(s) elapsed)..."
    }

    if ($process.ExitCode -ne 0) {
        throw "dotnet run failed with exit code $($process.ExitCode)."
    }
}

if ($ContentionMatrix.IsPresent) {
    $counts =
        $ContentionProcessorCounts.Split(",", [System.StringSplitOptions]::RemoveEmptyEntries) |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -match '^\d+$' }

    if (($counts | Measure-Object).Count -eq 0) {
        Write-Error "Contention matrix enabled but no valid processor counts were provided."
        exit 1
    }

    $originalDotnetProcessorCount = [Environment]::GetEnvironmentVariable("DOTNET_PROCESSOR_COUNT")
    try {
        foreach ($count in $counts) {
            [Environment]::SetEnvironmentVariable("DOTNET_PROCESSOR_COUNT", $count)
            $matrixArtifacts = Join-Path $artifacts "cpu-$count"
            New-Item -ItemType Directory -Path $matrixArtifacts -Force | Out-Null
            Write-Host "Running contention profile DOTNET_PROCESSOR_COUNT=$count"
            Invoke-Benchmarks -targetArtifacts $matrixArtifacts
        }
    }
    finally {
        [Environment]::SetEnvironmentVariable("DOTNET_PROCESSOR_COUNT", $originalDotnetProcessorCount)
    }
}
else {
    Invoke-Benchmarks -targetArtifacts $artifacts
}

$searchRoots = @(
    $artifacts,
    (Join-Path $repoRoot "BenchmarkDotNet.Artifacts\results"),
    (Join-Path $repoRoot "BenchmarkDotNet.Artifacts")
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique

$comparisonFiles =
    $searchRoots |
    ForEach-Object {
        if (Test-Path $_) {
            Get-ChildItem -Path $_ -Filter comparison.md -Recurse -ErrorAction SilentlyContinue
        }
    } |
    Sort-Object -Property LastWriteTimeUtc, FullName -Descending -Unique

if (($comparisonFiles | Measure-Object).Count -eq 0) {
    Write-Host "No comparison.md generated."
    exit 1
}

Write-Host ""
if ($AllReports.IsPresent) {
    Write-Host "Comparison reports:"
    $comparisonFiles | ForEach-Object { Write-Host " - $($_.FullName)" }
}
else {
    $latest = $comparisonFiles | Select-Object -First 1
    Write-Host "Latest comparison report:"
    Write-Host " - $($latest.FullName)"
}
