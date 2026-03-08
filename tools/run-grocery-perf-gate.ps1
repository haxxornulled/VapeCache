param(
    [string[]]$Profiles = @("LowLatency", "FullTilt"),
    [int]$Trials = 3,
    [int]$WarmupTrials = 1,
    [int]$ShopperCount = 20000,
    [int]$MaxCartSize = 35,
    [int]$MaxDegree = 64,
    [int]$MuxConnections = 16,
    [int]$MuxInFlight = 8192,
    [ValidateSet("true", "false")]
    [string]$MuxAdaptiveCoalescing = "true",
    [ValidateSet("true", "false")]
    [string]$MuxSocketReader = "true",
    [ValidateSet("true", "false")]
    [string]$MuxDedicatedWorkers = "true",
    [ValidateSet("auto", "true", "false")]
    [string]$ServerGc = "true",
    [ValidateSet("optimized", "apples")]
    [string]$Track = "optimized",
    [double]$MinThroughputRatio = 0.97,
    [double]$LowLatencyMaxP99Ratio = 1.15,
    [double]$LowLatencyMaxP999Ratio = 1.20,
    [double]$FullTiltMaxP99Ratio = 1.30,
    [double]$FullTiltMaxP999Ratio = 1.35,
    [double]$MaxAllocRatio = 1.40,
    [double]$MaxAllocBytesPerShopper = 35000.0,
    [double]$LowLatencyMaxP99Ms = 15.0,
    [double]$LowLatencyMaxP999Ms = 25.0,
    [double]$FullTiltMaxP99Ms = 20.0,
    [double]$FullTiltMaxP999Ms = 35.0,
    [switch]$StrictTailRatios,
    [ValidateSet("Trace", "Debug", "Information", "Warning", "Error", "Critical", "None")]
    [string]$BenchLogLevel = "Debug",
    [ValidateSet("true", "false")]
    [string]$GroceryVerbose = "true",
    [string]$RedisHost = "127.0.0.1",
    [int]$RedisPort = 6379,
    [string]$RedisUsername = "",
    [string]$RedisPassword = ""
)

$ErrorActionPreference = "Stop"

if ($Trials -le 0) { throw "Trials must be greater than zero." }
if ($WarmupTrials -lt 0) { throw "WarmupTrials cannot be negative." }
if ($ShopperCount -le 0) { throw "ShopperCount must be greater than zero." }
if ($MaxCartSize -le 0) { throw "MaxCartSize must be greater than zero." }
if ($MaxDegree -lt 0) { throw "MaxDegree cannot be negative." }
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
                MaxP99Ms = [double]$LowLatencyMaxP99Ms
                MaxP999Ms = [double]$LowLatencyMaxP999Ms
            }
        }
        "fulltilt" {
            return @{
                MaxP99Ratio = [double]$FullTiltMaxP99Ratio
                MaxP999Ratio = [double]$FullTiltMaxP999Ratio
                MaxP99Ms = [double]$FullTiltMaxP99Ms
                MaxP999Ms = [double]$FullTiltMaxP999Ms
            }
        }
        default {
            # Conservative defaults for other profiles.
            return @{
                MaxP99Ratio = 1.40
                MaxP999Ratio = 1.50
                MaxP99Ms = 25.0
                MaxP999Ms = 40.0
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
if ($MaxDegree -gt 0) {
    $env:VAPECACHE_BENCH_MAX_DEGREE = "$MaxDegree"
}
else {
    Remove-Item Env:VAPECACHE_BENCH_MAX_DEGREE -ErrorAction SilentlyContinue
}
$env:VAPECACHE_BENCH_MUX_CONNECTIONS = "$MuxConnections"
$env:VAPECACHE_BENCH_MUX_INFLIGHT = "$MuxInFlight"
$env:VAPECACHE_BENCH_MUX_COALESCE = "true"
$env:VAPECACHE_BENCH_MUX_ADAPTIVE_COALESCING = $MuxAdaptiveCoalescing.ToLowerInvariant()
$env:VAPECACHE_BENCH_SOCKET_RESP_READER = $MuxSocketReader.ToLowerInvariant()
$env:VAPECACHE_BENCH_DEDICATED_LANE_WORKERS = $MuxDedicatedWorkers.ToLowerInvariant()
$env:VAPECACHE_BENCH_MUX_RESPONSE_TIMEOUT_MS = "0"
$env:VAPECACHE_BENCH_LOG_LEVEL = $BenchLogLevel
$env:VAPECACHE_GROCERYSTORE_VERBOSE = $GroceryVerbose.ToLowerInvariant()
if ($ServerGc -eq "true") {
    $env:DOTNET_GCServer = "1"
}
elseif ($ServerGc -eq "false") {
    $env:DOTNET_GCServer = "0"
}

Write-Host "Grocery perf gate configuration:"
Write-Host "  Trials=$Trials WarmupTrials=$WarmupTrials Shoppers=$ShopperCount MaxCartSize=$MaxCartSize Track=$Track"
Write-Host "  MaxDegree=$(if ($MaxDegree -gt 0) { $MaxDegree } else { "auto" }) Profile(s)=$($Profiles -join ', ')"
Write-Host "  MuxConnections=$MuxConnections MuxInFlight=$MuxInFlight MuxCoalesce=true AdaptiveCoalesce=$($env:VAPECACHE_BENCH_MUX_ADAPTIVE_COALESCING) SocketReader=$($env:VAPECACHE_BENCH_SOCKET_RESP_READER) DedicatedWorkers=$($env:VAPECACHE_BENCH_DEDICATED_LANE_WORKERS)"
Write-Host "  Logging BenchLogLevel=$($env:VAPECACHE_BENCH_LOG_LEVEL) GroceryVerbose=$($env:VAPECACHE_GROCERYSTORE_VERBOSE)"
Write-Host "  DOTNET_GCServer=$(if ([string]::IsNullOrWhiteSpace($env:DOTNET_GCServer)) { "default" } else { $env:DOTNET_GCServer })"
Write-Host "  StrictTailRatios=$($StrictTailRatios.IsPresent)"

$summary = New-Object System.Collections.Generic.List[object]
$violations = New-Object System.Collections.Generic.List[string]

foreach ($profile in $Profiles) {
    $thresholds = Get-ProfileThresholds -profile $profile
    $env:VAPECACHE_BENCH_MUX_PROFILE = $profile
    $profileRows = New-Object System.Collections.Generic.List[object]

    Write-Host ""
    Write-Host "Running grocery perf gate for profile '$profile'..."
    if ($WarmupTrials -gt 0) {
        Write-Host "  Warmup $WarmupTrials trial(s)..."
        for ($warmup = 1; $warmup -le $WarmupTrials; $warmup++) {
            $warmupOutput = dotnet run --project "$projectPath" -c Release --no-build -- --compare 2>&1
            $warmupFile = Join-Path $artifactsRoot ("{0}-warmup-{1}.log" -f $profile.ToLowerInvariant(), $warmup)
            $warmupOutput | Out-File -FilePath $warmupFile -Encoding utf8
        }
    }

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
        $vapeAllocPerShopper = Parse-Metric -map $vape -key "AllocBytesPerShopper"
        $serAllocPerShopper = Parse-Metric -map $ser -key "AllocBytesPerShopper"

        $throughputRatio = if ($serThroughput -gt 0) { $vapeThroughput / $serThroughput } else { [double]::PositiveInfinity }
        $p99Ratio = if ($serP99 -gt 0) { $vapeP99 / $serP99 } else { [double]::PositiveInfinity }
        $p999Ratio = if ($serP999 -gt 0) { $vapeP999 / $serP999 } else { [double]::PositiveInfinity }
        $allocRatio = if ($serAllocPerShopper -gt 0) { $vapeAllocPerShopper / $serAllocPerShopper } else { [double]::PositiveInfinity }

        $profileRows.Add([pscustomobject]@{
            Profile = $profile
            Trial = $trial
            ThroughputRatio = [Math]::Round($throughputRatio, 3)
            P99Ratio = [Math]::Round($p99Ratio, 3)
            P999Ratio = [Math]::Round($p999Ratio, 3)
            AllocRatio = [Math]::Round($allocRatio, 3)
            VapeAllocBytesPerShopper = [Math]::Round($vapeAllocPerShopper, 2)
            VapeP99Ms = [Math]::Round($vapeP99, 3)
            VapeP999Ms = [Math]::Round($vapeP999, 3)
        }) | Out-Null
    }

    if ($profileRows.Count -eq 0) {
        continue
    }

    $medianThroughput = Get-Median -values @($profileRows | ForEach-Object { [double]$_.ThroughputRatio })
    $medianP99 = Get-Median -values @($profileRows | ForEach-Object { [double]$_.P99Ratio })
    $medianP999 = Get-Median -values @($profileRows | ForEach-Object { [double]$_.P999Ratio })
    $medianAllocRatio = Get-Median -values @($profileRows | ForEach-Object { [double]$_.AllocRatio })
    $medianVapeAlloc = Get-Median -values @($profileRows | ForEach-Object { [double]$_.VapeAllocBytesPerShopper })
    $medianVapeP99Ms = Get-Median -values @($profileRows | ForEach-Object { [double]$_.VapeP99Ms })
    $medianVapeP999Ms = Get-Median -values @($profileRows | ForEach-Object { [double]$_.VapeP999Ms })

    $summary.Add([pscustomobject]@{
        Profile = $profile
        Trials = $profileRows.Count
        ThroughputRatioMedian = [Math]::Round($medianThroughput, 3)
        P99RatioMedian = [Math]::Round($medianP99, 3)
        P999RatioMedian = [Math]::Round($medianP999, 3)
        AllocRatioMedian = [Math]::Round($medianAllocRatio, 3)
        VapeAllocBytesPerShopperMedian = [Math]::Round($medianVapeAlloc, 2)
        VapeP99MsMedian = [Math]::Round($medianVapeP99Ms, 3)
        VapeP999MsMedian = [Math]::Round($medianVapeP999Ms, 3)
        MinThroughputRatio = [double]$MinThroughputRatio
        MaxP99Ratio = [double]$thresholds.MaxP99Ratio
        MaxP999Ratio = [double]$thresholds.MaxP999Ratio
        MaxP99Ms = [double]$thresholds.MaxP99Ms
        MaxP999Ms = [double]$thresholds.MaxP999Ms
    }) | Out-Null

    if ($medianThroughput -lt $MinThroughputRatio) {
        $violations.Add(
            ("Profile '{0}': median throughput ratio {1:N3} below minimum {2:N3}" -f $profile, $medianThroughput, $MinThroughputRatio))
    }

    if ($medianAllocRatio -gt $MaxAllocRatio) {
        $violations.Add(
            ("Profile '{0}': median alloc ratio {1:N3} above maximum {2:N3}" -f $profile, $medianAllocRatio, $MaxAllocRatio))
    }

    if ($medianVapeAlloc -gt $MaxAllocBytesPerShopper) {
        $violations.Add(
            ("Profile '{0}': median Vape alloc/shopper {1:N2}B above maximum {2:N2}B" -f $profile, $medianVapeAlloc, $MaxAllocBytesPerShopper))
    }

    if ($medianVapeP99Ms -gt $thresholds.MaxP99Ms) {
        $violations.Add(
            ("Profile '{0}': median Vape p99 {1:N3}ms above maximum {2:N3}ms" -f $profile, $medianVapeP99Ms, $thresholds.MaxP99Ms))
    }

    if ($medianVapeP999Ms -gt $thresholds.MaxP999Ms) {
        $violations.Add(
            ("Profile '{0}': median Vape p999 {1:N3}ms above maximum {2:N3}ms" -f $profile, $medianVapeP999Ms, $thresholds.MaxP999Ms))
    }

    if ($StrictTailRatios.IsPresent) {
        if ($medianP99 -gt $thresholds.MaxP99Ratio) {
            $violations.Add(
                ("Profile '{0}': median p99 ratio {1:N3} above maximum {2:N3}" -f $profile, $medianP99, $thresholds.MaxP99Ratio))
        }

        if ($medianP999 -gt $thresholds.MaxP999Ratio) {
            $violations.Add(
                ("Profile '{0}': median p999 ratio {1:N3} above maximum {2:N3}" -f $profile, $medianP999, $thresholds.MaxP999Ratio))
        }
    }
    else {
        if ($medianP99 -gt $thresholds.MaxP99Ratio) {
            Write-Warning ("Profile '{0}': median p99 ratio {1:N3} above advisory ratio {2:N3} (strict ratio gate disabled)." -f $profile, $medianP99, $thresholds.MaxP99Ratio)
        }

        if ($medianP999 -gt $thresholds.MaxP999Ratio) {
            Write-Warning ("Profile '{0}': median p999 ratio {1:N3} above advisory ratio {2:N3} (strict ratio gate disabled)." -f $profile, $medianP999, $thresholds.MaxP999Ratio)
        }
    }

    Write-Host ("PERF-GATE|Scenario=Grocery/{0}|Trials={1}|ThroughputRatioMedian={2:N3}|P99RatioMedian={3:N3}|P999RatioMedian={4:N3}|AllocRatioMedian={5:N3}|VapeAllocBytesPerShopperMedian={6:N2}" -f
        $profile,
        $profileRows.Count,
        $medianThroughput,
        $medianP99,
        $medianP999,
        $medianAllocRatio,
        $medianVapeAlloc)
}

Write-Host ""
Write-Host "Grocery perf gate summary:"
$summary | Format-Table Profile, Trials, ThroughputRatioMedian, AllocRatioMedian, VapeAllocBytesPerShopperMedian, VapeP99MsMedian, VapeP999MsMedian, P99RatioMedian, P999RatioMedian, MinThroughputRatio, MaxP99Ms, MaxP999Ms -AutoSize

$summaryFile = Join-Path $artifactsRoot "summary.md"
@(
    "# Grocery Perf Gate Summary"
    ""
    ("Generated: {0} UTC" -f (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss"))
    ""
    "| Profile | Trials | Median Throughput Ratio | Median Alloc Ratio | Median Vape Alloc/Shopper (B) | Median Vape P99 (ms) | Median Vape P999 (ms) | Median P99 Ratio | Median P999 Ratio | Min Throughput Ratio | Max P99 (ms) | Max P999 (ms) |"
    "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|"
) + ($summary | ForEach-Object {
    "| $($_.Profile) | $($_.Trials) | $($_.ThroughputRatioMedian) | $($_.AllocRatioMedian) | $($_.VapeAllocBytesPerShopperMedian) | $($_.VapeP99MsMedian) | $($_.VapeP999MsMedian) | $($_.P99RatioMedian) | $($_.P999RatioMedian) | $($_.MinThroughputRatio) | $($_.MaxP99Ms) | $($_.MaxP999Ms) |"
}) | Set-Content -Path $summaryFile -Encoding utf8

Write-Host "Summary written to: $summaryFile"

if ($violations.Count -gt 0) {
    Write-Error "Grocery perf gate failed:`n$($violations -join "`n")"
    exit 1
}

Write-Host "Grocery perf gate passed."
exit 0
