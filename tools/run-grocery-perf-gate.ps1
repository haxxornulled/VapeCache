param(
    [string[]]$Profiles = @("LowLatency", "FullTilt"),
    [int]$Trials = 3,
    [int]$ShopperCount = 20000,
    [int]$MaxCartSize = 35,
    [int]$MuxConnections = 12,
    [int]$MuxInFlight = 4096,
    [ValidateSet("optimized", "apples")]
    [string]$Track = "optimized",
    [double]$MinThroughputRatio = 1.00,
    [double]$LowLatencyMaxP99Ratio = 1.15,
    [double]$LowLatencyMaxP999Ratio = 1.20,
    [double]$FullTiltMaxP99Ratio = 1.30,
    [double]$FullTiltMaxP999Ratio = 1.35,
    [string]$RedisHost = "127.0.0.1",
    [int]$RedisPort = 6379,
    [string]$RedisUsername = "",
    [string]$RedisPassword = ""
)

$ErrorActionPreference = "Stop"

if ($Trials -le 0) { throw "Trials must be greater than zero." }
if ($ShopperCount -le 0) { throw "ShopperCount must be greater than zero." }
if ($MaxCartSize -le 0) { throw "MaxCartSize must be greater than zero." }
if ($MuxConnections -le 0) { throw "MuxConnections must be greater than zero." }
if ($MuxInFlight -le 0) { throw "MuxInFlight must be greater than zero." }

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repoRoot "VapeCache.Console/VapeCache.Console.csproj"
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$artifactsRoot = Join-Path $repoRoot "BenchmarkDotNet.Artifacts/grocery-perf-gate/$timestamp"
New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null

function Get-Median([double[]]$values) {
    if ($values.Count -eq 0) { return 0.0 }
    $sorted = $values | Sort-Object
    $count = $sorted.Count
    $middle = [int][Math]::Floor($count / 2.0)
    if (($count % 2) -eq 1) {
        return [double]$sorted[$middle]
    }

    return ([double]$sorted[$middle - 1] + [double]$sorted[$middle]) / 2.0
}

function Parse-ResultLine([string]$line) {
    $parts = $line.Split('|')
    $map = @{}
    foreach ($part in $parts) {
        if ($part -notmatch '=') { continue }
        $kv = $part.Split('=', 2)
        $map[$kv[0]] = $kv[1]
    }
    return $map
}

function Parse-Metric([hashtable]$map, [string]$key) {
    if (-not $map.ContainsKey($key)) { throw "Missing metric '$key' in RESULT line." }
    $value = 0.0
    $ok = [double]::TryParse(
        $map[$key],
        [System.Globalization.NumberStyles]::Float,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [ref]$value)
    if (-not $ok) {
        throw "Unable to parse metric '$key' value '$($map[$key])'."
    }

    return $value
}

function Get-ProfileThresholds([string]$profile) {
    switch ($profile.ToLowerInvariant()) {
        "lowlatency" {
            return @{
                MaxP99Ratio = [double]$LowLatencyMaxP99Ratio
                MaxP999Ratio = [double]$LowLatencyMaxP999Ratio
            }
        }
        "fulltilt" {
            return @{
                MaxP99Ratio = [double]$FullTiltMaxP99Ratio
                MaxP999Ratio = [double]$FullTiltMaxP999Ratio
            }
        }
        default {
            # Conservative defaults for other profiles.
            return @{
                MaxP99Ratio = 1.40
                MaxP999Ratio = 1.50
            }
        }
    }
}

Write-Host "Building GroceryStore comparison project..."
dotnet build "$projectPath" -c Release --nologo | Out-Host

$env:RedisConnection__Host = $RedisHost
$env:RedisConnection__Port = "$RedisPort"
$env:RedisConnection__Username = $RedisUsername
$env:RedisConnection__Password = $RedisPassword
$env:VAPECACHE_RUN_COMPARISON = "true"
$env:VAPECACHE_BENCH_SHOPPERS = "$ShopperCount"
$env:VAPECACHE_BENCH_TRACK = $Track
$env:VAPECACHE_MAX_CART_SIZE = "$MaxCartSize"
$env:VAPECACHE_BENCH_MUX_CONNECTIONS = "$MuxConnections"
$env:VAPECACHE_BENCH_MUX_INFLIGHT = "$MuxInFlight"
$env:VAPECACHE_BENCH_MUX_COALESCE = "true"
$env:VAPECACHE_BENCH_MUX_RESPONSE_TIMEOUT_MS = "0"

$summary = New-Object System.Collections.Generic.List[object]
$violations = New-Object System.Collections.Generic.List[string]

foreach ($profile in $Profiles) {
    $thresholds = Get-ProfileThresholds -profile $profile
    $env:VAPECACHE_BENCH_MUX_PROFILE = $profile
    $profileRows = New-Object System.Collections.Generic.List[object]

    Write-Host ""
    Write-Host "Running grocery perf gate for profile '$profile'..."
    for ($trial = 1; $trial -le $Trials; $trial++) {
        Write-Host "  Trial $trial/$Trials..."
        $output = dotnet run --project "$projectPath" -c Release --no-build -- --compare 2>&1
        $outFile = Join-Path $artifactsRoot ("{0}-trial-{1}.log" -f $profile.ToLowerInvariant(), $trial)
        $output | Out-File -FilePath $outFile -Encoding utf8

        $resultLines = $output | Where-Object { $_ -like "RESULT|*" }
        $vapeLine = $resultLines | Where-Object { $_ -like "RESULT|Provider=VapeCache*" } | Select-Object -Last 1
        $serLine = $resultLines | Where-Object { $_ -like "RESULT|Provider=StackExchange.Redis*" } | Select-Object -Last 1

        if ([string]::IsNullOrWhiteSpace($vapeLine) -or [string]::IsNullOrWhiteSpace($serLine)) {
            $violations.Add("Profile '$profile' trial ${trial}: missing RESULT lines in $outFile")
            continue
        }

        $vape = Parse-ResultLine -line $vapeLine
        $ser = Parse-ResultLine -line $serLine
        $vapeThroughput = Parse-Metric -map $vape -key "Throughput"
        $serThroughput = Parse-Metric -map $ser -key "Throughput"
        $vapeP99 = Parse-Metric -map $vape -key "P99Ms"
        $serP99 = Parse-Metric -map $ser -key "P99Ms"
        $vapeP999 = Parse-Metric -map $vape -key "P999Ms"
        $serP999 = Parse-Metric -map $ser -key "P999Ms"

        $throughputRatio = if ($serThroughput -gt 0) { $vapeThroughput / $serThroughput } else { [double]::PositiveInfinity }
        $p99Ratio = if ($serP99 -gt 0) { $vapeP99 / $serP99 } else { [double]::PositiveInfinity }
        $p999Ratio = if ($serP999 -gt 0) { $vapeP999 / $serP999 } else { [double]::PositiveInfinity }

        $profileRows.Add([pscustomobject]@{
            Profile = $profile
            Trial = $trial
            ThroughputRatio = [Math]::Round($throughputRatio, 3)
            P99Ratio = [Math]::Round($p99Ratio, 3)
            P999Ratio = [Math]::Round($p999Ratio, 3)
        }) | Out-Null
    }

    if ($profileRows.Count -eq 0) {
        continue
    }

    $medianThroughput = Get-Median -values @($profileRows | ForEach-Object { [double]$_.ThroughputRatio })
    $medianP99 = Get-Median -values @($profileRows | ForEach-Object { [double]$_.P99Ratio })
    $medianP999 = Get-Median -values @($profileRows | ForEach-Object { [double]$_.P999Ratio })

    $summary.Add([pscustomobject]@{
        Profile = $profile
        Trials = $profileRows.Count
        ThroughputRatioMedian = [Math]::Round($medianThroughput, 3)
        P99RatioMedian = [Math]::Round($medianP99, 3)
        P999RatioMedian = [Math]::Round($medianP999, 3)
        MinThroughputRatio = [double]$MinThroughputRatio
        MaxP99Ratio = [double]$thresholds.MaxP99Ratio
        MaxP999Ratio = [double]$thresholds.MaxP999Ratio
    }) | Out-Null

    if ($medianThroughput -lt $MinThroughputRatio) {
        $violations.Add(
            ("Profile '{0}': median throughput ratio {1:N3} below minimum {2:N3}" -f $profile, $medianThroughput, $MinThroughputRatio))
    }

    if ($medianP99 -gt $thresholds.MaxP99Ratio) {
        $violations.Add(
            ("Profile '{0}': median p99 ratio {1:N3} above maximum {2:N3}" -f $profile, $medianP99, $thresholds.MaxP99Ratio))
    }

    if ($medianP999 -gt $thresholds.MaxP999Ratio) {
        $violations.Add(
            ("Profile '{0}': median p999 ratio {1:N3} above maximum {2:N3}" -f $profile, $medianP999, $thresholds.MaxP999Ratio))
    }
}

Write-Host ""
Write-Host "Grocery perf gate summary:"
$summary | Format-Table Profile, Trials, ThroughputRatioMedian, P99RatioMedian, P999RatioMedian, MinThroughputRatio, MaxP99Ratio, MaxP999Ratio -AutoSize

$summaryFile = Join-Path $artifactsRoot "summary.md"
@(
    "# Grocery Perf Gate Summary"
    ""
    ("Generated: {0} UTC" -f (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss"))
    ""
    "| Profile | Trials | Median Throughput Ratio | Median P99 Ratio | Median P999 Ratio | Min Throughput Ratio | Max P99 Ratio | Max P999 Ratio |"
    "|---|---:|---:|---:|---:|---:|---:|---:|"
) + ($summary | ForEach-Object {
    "| $($_.Profile) | $($_.Trials) | $($_.ThroughputRatioMedian) | $($_.P99RatioMedian) | $($_.P999RatioMedian) | $($_.MinThroughputRatio) | $($_.MaxP99Ratio) | $($_.MaxP999Ratio) |"
}) | Set-Content -Path $summaryFile -Encoding utf8

Write-Host "Summary written to: $summaryFile"

if ($violations.Count -gt 0) {
    Write-Error "Grocery perf gate failed:`n$($violations -join "`n")"
    exit 1
}

Write-Host "Grocery perf gate passed."
exit 0
