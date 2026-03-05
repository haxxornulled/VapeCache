param(
    [int]$Trials = 5,
    [int]$ShopperCount = 50000,
    [int]$MaxCartSize = 40,
    [int]$MaxDegree = 72,
    [ValidateSet("optimized", "apples", "both")]
    [string]$Track = "both",
    [ValidateSet("FullTilt", "Balanced", "LowLatency", "Custom")]
    [string]$MuxProfile = "FullTilt",
    [int]$MuxConnections = 8,
    [int]$MuxInFlight = 8192,
    [ValidateSet("true", "false")]
    [string]$MuxCoalesce = "true",
    [ValidateSet("true", "false")]
    [string]$MuxAdaptiveCoalescing = "true",
    [ValidateSet("true", "false")]
    [string]$MuxSocketReader = "true",
    [ValidateSet("true", "false")]
    [string]$MuxDedicatedWorkers = "true",
    [int]$MuxResponseTimeoutMs = 0,
    [ValidateSet("true", "false")]
    [string]$CleanupRunKeys = "true",
    [ValidateSet("auto", "true", "false")]
    [string]$ServerGc = "true",
    [string]$RedisConnectionString = "",
    [string]$RedisHost = "127.0.0.1",
    [int]$RedisPort = 6379,
    [string]$RedisUsername = "",
    [string]$RedisPassword = "",
    [switch]$SkipBuild,
    [switch]$RequireHostIsolation,
    [double]$MaxHostCpuPercent = 35,
    [int]$StableCpuSamples = 8,
    [int]$MaxHostIsolationWaitSeconds = 180,
    [int]$PauseBetweenTrialsMs = 250,
    [ValidateSet("Trace", "Debug", "Information", "Warning", "Error", "Critical", "None")]
    [string]$BenchLogLevel = "Debug",
    [ValidateSet("true", "false")]
    [string]$GroceryVerbose = "true",
    [double]$FailBelowRatio = 1.0,
    [switch]$DisableTrackDefaults
)

$ErrorActionPreference = "Stop"

if ($Trials -le 0) {
    throw "Trials must be greater than zero."
}

if ($ShopperCount -le 0) {
    throw "ShopperCount must be greater than zero."
}

if ($MaxCartSize -le 0) {
    throw "MaxCartSize must be greater than zero."
}

if ($MaxDegree -lt 0) {
    throw "MaxDegree cannot be negative."
}

if ($MuxConnections -le 0) {
    throw "MuxConnections must be greater than zero."
}

if ($MuxInFlight -le 0) {
    throw "MuxInFlight must be greater than zero."
}

if ($MaxHostCpuPercent -le 0) {
    throw "MaxHostCpuPercent must be greater than zero."
}

if ($StableCpuSamples -le 0) {
    throw "StableCpuSamples must be greater than zero."
}

if ($MaxHostIsolationWaitSeconds -le 0) {
    throw "MaxHostIsolationWaitSeconds must be greater than zero."
}

if ($PauseBetweenTrialsMs -lt 0) {
    throw "PauseBetweenTrialsMs cannot be negative."
}

if ($ServerGc -eq "true") {
    $env:DOTNET_GCServer = "1"
}
elseif ($ServerGc -eq "false") {
    $env:DOTNET_GCServer = "0"
}

$useConnectionString = -not [string]::IsNullOrWhiteSpace($RedisConnectionString)
$authMode = if ($useConnectionString) {
    "connection-string"
}
elseif ([string]::IsNullOrWhiteSpace($RedisPassword)) {
    "none"
}
elseif ([string]::IsNullOrWhiteSpace($RedisUsername)) {
    "password-only"
}
else {
    "acl"
}

if ($RequireHostIsolation -and -not $useConnectionString) {
    if ($RedisHost -eq "127.0.0.1" -or $RedisHost -eq "localhost") {
        throw "RequireHostIsolation is enabled but RedisHost is local ($RedisHost). Use a dedicated remote Redis endpoint."
    }
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repoRoot "VapeCache.Console\VapeCache.Console.csproj"

function Get-HostCpuPercent {
    try {
        $sample = Get-Counter '\Processor(_Total)\% Processor Time' -SampleInterval 1 -MaxSamples 1
        return [double]$sample.CounterSamples[0].CookedValue
    }
    catch {
        return [double]::NaN
    }
}

function Wait-ForHostIsolation {
    param(
        [double]$MaxCpu,
        [int]$StableSamplesRequired,
        [int]$MaxWaitSeconds
    )

    if (-not $RequireHostIsolation) {
        return
    }

    $stableSamples = 0
    $deadline = (Get-Date).AddSeconds($MaxWaitSeconds)
    while ((Get-Date) -lt $deadline) {
        $cpu = Get-HostCpuPercent
        if ([double]::IsNaN($cpu)) {
            Write-Warning "Unable to sample host CPU usage via Get-Counter. Continuing without host CPU gate."
            return
        }

        if ($cpu -le $MaxCpu) {
            $stableSamples++
            if ($stableSamples -ge $StableSamplesRequired) {
                Write-Host ("Isolation gate passed: host CPU <= {0:N1}% for {1} consecutive samples." -f $MaxCpu, $StableSamplesRequired)
                return
            }
        }
        else {
            $stableSamples = 0
        }
    }

    throw ("Host isolation gate failed: CPU never stabilized at <= {0:N1}% for {1} consecutive samples within {2}s." -f $MaxCpu, $StableSamplesRequired, $MaxWaitSeconds)
}

function Get-EffectiveMuxSettings([string]$RunTrack) {
    $effectiveProfile = $MuxProfile
    $effectiveConnections = $MuxConnections
    $effectiveInFlight = $MuxInFlight
    $effectiveCoalesce = $MuxCoalesce.ToLowerInvariant()
    $effectiveAdaptive = $MuxAdaptiveCoalescing.ToLowerInvariant()
    $effectiveSocketReader = $MuxSocketReader.ToLowerInvariant()
    $effectiveDedicatedWorkers = $MuxDedicatedWorkers.ToLowerInvariant()
    $effectiveResponseTimeoutMs = "$MuxResponseTimeoutMs"

    if (-not $DisableTrackDefaults -and $RunTrack -eq "apples") {
        if (-not $PSBoundParameters.ContainsKey("MuxConnections")) {
            # Apples-to-apples workload favors fewer connections to reduce fan-out overhead.
            $effectiveConnections = 1
        }

        if (-not $PSBoundParameters.ContainsKey("MuxAdaptiveCoalescing")) {
            # Adaptive coalescing can add burst jitter on parity workloads.
            $effectiveAdaptive = "false"
        }
    }

    return [pscustomobject]@{
        Profile = $effectiveProfile
        Connections = $effectiveConnections
        InFlight = $effectiveInFlight
        Coalesce = $effectiveCoalesce
        Adaptive = $effectiveAdaptive
        SocketReader = $effectiveSocketReader
        DedicatedWorkers = $effectiveDedicatedWorkers
        ResponseTimeoutMs = $effectiveResponseTimeoutMs
    }
}

function Set-MuxEnvironment([pscustomobject]$Settings) {
    $env:VAPECACHE_BENCH_MUX_PROFILE = "$($Settings.Profile)"
    $env:VAPECACHE_BENCH_MUX_CONNECTIONS = "$($Settings.Connections)"
    $env:VAPECACHE_BENCH_MUX_INFLIGHT = "$($Settings.InFlight)"
    $env:VAPECACHE_BENCH_MUX_COALESCE = "$($Settings.Coalesce)"
    $env:VAPECACHE_BENCH_MUX_ADAPTIVE_COALESCING = "$($Settings.Adaptive)"
    $env:VAPECACHE_BENCH_SOCKET_RESP_READER = "$($Settings.SocketReader)"
    $env:VAPECACHE_BENCH_DEDICATED_LANE_WORKERS = "$($Settings.DedicatedWorkers)"
    $env:VAPECACHE_BENCH_MUX_RESPONSE_TIMEOUT_MS = "$($Settings.ResponseTimeoutMs)"
}

$env:VAPECACHE_RUN_COMPARISON = "true"
$env:VAPECACHE_BENCH_SHOPPERS = "$ShopperCount"
$env:VAPECACHE_MAX_CART_SIZE = "$MaxCartSize"
$env:VAPECACHE_BENCH_TRACK = $Track
$env:VAPECACHE_BENCH_CLEANUP_RUN_KEYS = $CleanupRunKeys.ToLowerInvariant()
$env:VAPECACHE_BENCH_LOG_LEVEL = $BenchLogLevel
$env:VAPECACHE_GROCERYSTORE_VERBOSE = $GroceryVerbose.ToLowerInvariant()
if ($MaxDegree -gt 0) {
    $env:VAPECACHE_BENCH_MAX_DEGREE = "$MaxDegree"
}
else {
    Remove-Item Env:VAPECACHE_BENCH_MAX_DEGREE -ErrorAction SilentlyContinue
}
if ($useConnectionString) {
    $env:RedisConnection__ConnectionString = $RedisConnectionString
    $env:RedisConnection__Host = ""
    $env:RedisConnection__Port = ""
    $env:RedisConnection__Username = ""
    $env:RedisConnection__Password = ""
}
else {
    $env:RedisConnection__ConnectionString = ""
    $env:RedisConnection__Host = $RedisHost
    $env:RedisConnection__Port = "$RedisPort"
    $env:RedisConnection__Username = $RedisUsername
    $env:RedisConnection__Password = $RedisPassword
}

Write-Host "Grocery head-to-head benchmark"
Write-Host "Trials: $Trials"
Write-Host "Shoppers: $ShopperCount"
Write-Host "Max cart size: $MaxCartSize"
Write-Host "Max degree: $(if ($MaxDegree -gt 0) { "$MaxDegree (override)" } else { "auto" })"
Write-Host "Track: $Track"
if ($Track -eq "both") {
    Write-Host "Both-track isolation: enabled"
}
Write-Host "Track defaults: $(if ($DisableTrackDefaults) { "disabled" } else { "enabled" })"
if ($Track -eq "both") {
    $applesMux = Get-EffectiveMuxSettings -RunTrack "apples"
    $optimizedMux = Get-EffectiveMuxSettings -RunTrack "optimized"
    Write-Host "Mux (apples): Profile=$($applesMux.Profile) Connections=$($applesMux.Connections) InFlight=$($applesMux.InFlight) Coalesce=$($applesMux.Coalesce) Adaptive=$($applesMux.Adaptive) SocketReader=$($applesMux.SocketReader) DedicatedWorkers=$($applesMux.DedicatedWorkers) TimeoutMs=$($applesMux.ResponseTimeoutMs)"
    Write-Host "Mux (optimized): Profile=$($optimizedMux.Profile) Connections=$($optimizedMux.Connections) InFlight=$($optimizedMux.InFlight) Coalesce=$($optimizedMux.Coalesce) Adaptive=$($optimizedMux.Adaptive) SocketReader=$($optimizedMux.SocketReader) DedicatedWorkers=$($optimizedMux.DedicatedWorkers) TimeoutMs=$($optimizedMux.ResponseTimeoutMs)"
}
else {
    $selectedMux = Get-EffectiveMuxSettings -RunTrack $Track
    Set-MuxEnvironment -Settings $selectedMux
    Write-Host "Mux: Profile=$($selectedMux.Profile) Connections=$($selectedMux.Connections) InFlight=$($selectedMux.InFlight) Coalesce=$($selectedMux.Coalesce) Adaptive=$($selectedMux.Adaptive) SocketReader=$($selectedMux.SocketReader) DedicatedWorkers=$($selectedMux.DedicatedWorkers) TimeoutMs=$($selectedMux.ResponseTimeoutMs)"
}
Write-Host "Cleanup: RunKeys=$($env:VAPECACHE_BENCH_CLEANUP_RUN_KEYS)"
Write-Host "Logging: BenchLogLevel=$($env:VAPECACHE_BENCH_LOG_LEVEL) GroceryVerbose=$($env:VAPECACHE_GROCERYSTORE_VERBOSE)"
Write-Host "DOTNET_GCServer: $(if ([string]::IsNullOrWhiteSpace($env:DOTNET_GCServer)) { "default" } else { $env:DOTNET_GCServer })"
if ($useConnectionString) {
    Write-Host "Redis: Source=ConnectionString Auth=$authMode"
}
else {
    Write-Host "Redis: Endpoint=$RedisHost`:$RedisPort Auth=$authMode"
}
Write-Host ""

if ($RequireHostIsolation) {
    Write-Host "Isolation gate: enabled (MaxHostCpuPercent=$MaxHostCpuPercent, StableCpuSamples=$StableCpuSamples, MaxWaitSeconds=$MaxHostIsolationWaitSeconds)"
    try {
        [System.Diagnostics.Process]::GetCurrentProcess().PriorityClass = [System.Diagnostics.ProcessPriorityClass]::High
        Write-Host "Isolation gate: set benchmark runner process priority to High."
    }
    catch {
        Write-Warning "Isolation gate: unable to set process priority to High. Continuing."
    }

    Wait-ForHostIsolation -MaxCpu $MaxHostCpuPercent -StableSamplesRequired $StableCpuSamples -MaxWaitSeconds $MaxHostIsolationWaitSeconds
    Write-Host ""
}

if (-not $SkipBuild) {
    Write-Host "Building Release binaries..."
    dotnet build "$projectPath" -c Release
    Write-Host ""
}

function Get-Median([double[]]$values) {
    if ($values.Count -eq 0) {
        return 0.0
    }

    $sorted = $values | Sort-Object
    $count = $sorted.Count
    $middle = [int][Math]::Floor($count / 2.0)
    if (($count % 2) -eq 1) {
        return [double]$sorted[$middle]
    }

    return ([double]$sorted[$middle - 1] + [double]$sorted[$middle]) / 2.0
}

function Get-StdDev([double[]]$values) {
    if ($values.Count -lt 2) {
        return 0.0
    }

    $mean = ($values | Measure-Object -Average).Average
    $sum = 0.0
    foreach ($v in $values) {
        $delta = $v - $mean
        $sum += ($delta * $delta)
    }

    return [Math]::Sqrt($sum / ($values.Count - 1))
}

function Get-TrackSummary([string]$trackName, [System.Collections.Generic.List[object]]$rows) {
    if ($rows.Count -eq 0) {
        return $null
    }

    $vapeValues = @($rows | ForEach-Object { [double]$_.VapeThroughput })
    $serValues = @($rows | ForEach-Object { [double]$_.SerThroughput })
    $ratioValues = @($rows | ForEach-Object { [double]$_.Ratio })
    $vapeMedian = Get-Median -values $vapeValues
    $serMedian = Get-Median -values $serValues
    $ratioOfMedians = if ($serMedian -gt 0) { $vapeMedian / $serMedian } else { [double]::PositiveInfinity }
    $ratioMedian = Get-Median -values $ratioValues
    $ratioCov = if ($ratioMedian -ne 0) { (Get-StdDev -values $ratioValues) / $ratioMedian } else { 0.0 }
    $ratioMin = ($ratioValues | Measure-Object -Minimum).Minimum
    $ratioMax = ($ratioValues | Measure-Object -Maximum).Maximum

    return [pscustomobject]@{
        Track = $trackName
        Trials = $rows.Count
        VapeMedian = [Math]::Round($vapeMedian, 0)
        SerMedian = [Math]::Round($serMedian, 0)
        MedianRatio = [Math]::Round($ratioMedian, 3)
        RatioOfMedians = [Math]::Round($ratioOfMedians, 3)
        RatioCoV = [Math]::Round($ratioCov * 100.0, 1)
        RatioSpread = ("{0:N3} .. {1:N3}" -f $ratioMin, $ratioMax)
    }
}

function Parse-BenchmarkOutput([object[]]$OutputLines) {
    $throughputPattern = '^Throughput \(shoppers/sec\)\s+([0-9,]+(?:\.[0-9]+)?)\s+([0-9,]+(?:\.[0-9]+)?)\b'
    $trackPattern = '^Track:\s*(.+)$'
    $parsedByTrack = @{}
    $currentTrack = "single"

    foreach ($line in $OutputLines) {
        $text = "$line"
        $trackMatch = [regex]::Match($text, $trackPattern)
        if ($trackMatch.Success) {
            $currentTrack = $trackMatch.Groups[1].Value.Trim()
            continue
        }

        $throughputMatch = [regex]::Match($text, $throughputPattern)
        if (-not $throughputMatch.Success) {
            continue
        }

        $parsedByTrack[$currentTrack] = [pscustomobject]@{
            Vape = [double](($throughputMatch.Groups[1].Value) -replace ",", "")
            Ser = [double](($throughputMatch.Groups[2].Value) -replace ",", "")
        }
    }

    return $parsedByTrack
}

function Invoke-BenchmarkRun([string]$RunTrack) {
    $hadTrack = Test-Path Env:VAPECACHE_BENCH_TRACK
    $previousTrack = $env:VAPECACHE_BENCH_TRACK
    $muxEnvNames = @(
        "VAPECACHE_BENCH_MUX_PROFILE",
        "VAPECACHE_BENCH_MUX_CONNECTIONS",
        "VAPECACHE_BENCH_MUX_INFLIGHT",
        "VAPECACHE_BENCH_MUX_COALESCE",
        "VAPECACHE_BENCH_MUX_ADAPTIVE_COALESCING",
        "VAPECACHE_BENCH_SOCKET_RESP_READER",
        "VAPECACHE_BENCH_DEDICATED_LANE_WORKERS",
        "VAPECACHE_BENCH_MUX_RESPONSE_TIMEOUT_MS"
    )
    $previousMuxEnv = @{}
    foreach ($name in $muxEnvNames) {
        $previousMuxEnv[$name] = [Environment]::GetEnvironmentVariable($name)
    }
    $env:VAPECACHE_BENCH_TRACK = $RunTrack
    $settings = Get-EffectiveMuxSettings -RunTrack $RunTrack
    Set-MuxEnvironment -Settings $settings
    try {
        Write-Host ("  [TrackConfig] {0}: Profile={1} Connections={2} InFlight={3} Coalesce={4} Adaptive={5} SocketReader={6} DedicatedWorkers={7} TimeoutMs={8}" -f $RunTrack, $settings.Profile, $settings.Connections, $settings.InFlight, $settings.Coalesce, $settings.Adaptive, $settings.SocketReader, $settings.DedicatedWorkers, $settings.ResponseTimeoutMs)
        $runOutput = @(dotnet run --project "$projectPath" -c Release --no-build -- --compare 2>&1)
        $parsed = Parse-BenchmarkOutput -OutputLines $runOutput
        return [pscustomobject]@{
            Output = $runOutput
            ParsedByTrack = $parsed
        }
    }
    finally {
        if ($hadTrack) {
            $env:VAPECACHE_BENCH_TRACK = $previousTrack
        }
        else {
            Remove-Item Env:VAPECACHE_BENCH_TRACK -ErrorAction SilentlyContinue
        }

        foreach ($name in $muxEnvNames) {
            $value = $previousMuxEnv[$name]
            if ([string]::IsNullOrEmpty($value)) {
                Remove-Item ("Env:" + $name) -ErrorAction SilentlyContinue
            }
            else {
                [Environment]::SetEnvironmentVariable($name, $value)
            }
        }
    }
}

$results = New-Object System.Collections.Generic.List[object]
$trackResults = @{
    ApplesToApples = New-Object System.Collections.Generic.List[object]
    OptimizedProductPath = New-Object System.Collections.Generic.List[object]
}
$isolateBoth = $Track -eq "both"

for ($trial = 1; $trial -le $Trials; $trial++) {
    if ($RequireHostIsolation) {
        Wait-ForHostIsolation -MaxCpu $MaxHostCpuPercent -StableSamplesRequired $StableCpuSamples -MaxWaitSeconds $MaxHostIsolationWaitSeconds
    }

    Write-Host "Run $trial/$Trials..."
    $output = @()
    $selected = $null
    $apples = $null
    $optimized = $null

    if ($isolateBoth) {
        $applesRun = Invoke-BenchmarkRun -RunTrack "apples"
        $optimizedRun = Invoke-BenchmarkRun -RunTrack "optimized"
        $output = @($applesRun.Output + $optimizedRun.Output)

        if ($applesRun.ParsedByTrack.Count -eq 0 -or $optimizedRun.ParsedByTrack.Count -eq 0) {
            Write-Host "Unable to parse isolated both-track throughput values on run $trial."
            $output | ForEach-Object { Write-Host $_ }
            exit 2
        }

        $apples = $applesRun.ParsedByTrack["ApplesToApples"]
        if ($null -eq $apples) {
            $apples = $applesRun.ParsedByTrack["single"]
        }

        $optimized = $optimizedRun.ParsedByTrack["OptimizedProductPath"]
        if ($null -eq $optimized) {
            $optimized = $optimizedRun.ParsedByTrack["single"]
        }
    }
    else {
        $run = Invoke-BenchmarkRun -RunTrack $Track
        $output = $run.Output
        $parsedByTrack = $run.ParsedByTrack

        if ($parsedByTrack.Count -eq 0) {
            Write-Host "Unable to parse throughput output on run $trial."
            $output | ForEach-Object { Write-Host $_ }
            exit 2
        }

        if ($Track -eq "apples") {
            $selected = $parsedByTrack["ApplesToApples"]
            if ($null -eq $selected) {
                $selected = $parsedByTrack["single"]
            }
        }
        else {
            $selected = $parsedByTrack["OptimizedProductPath"]
            if ($null -eq $selected) {
                $selected = $parsedByTrack["single"]
            }
        }
    }

    if ($Track -eq "both") {
        if ($null -ne $apples) {
            $applesRatio = if ($apples.Ser -gt 0) { $apples.Vape / $apples.Ser } else { [double]::PositiveInfinity }
            $trackResults["ApplesToApples"].Add([pscustomobject]@{
                Run = $trial
                VapeThroughput = [double]$apples.Vape
                SerThroughput = [double]$apples.Ser
                Ratio = [double]$applesRatio
            })
            Write-Host ("  ApplesToApples: Vape={0:N0} SER={1:N0} Ratio={2:N3}" -f $apples.Vape, $apples.Ser, $applesRatio)
        }
        if ($null -ne $optimized) {
            $optimizedRatio = if ($optimized.Ser -gt 0) { $optimized.Vape / $optimized.Ser } else { [double]::PositiveInfinity }
            $trackResults["OptimizedProductPath"].Add([pscustomobject]@{
                Run = $trial
                VapeThroughput = [double]$optimized.Vape
                SerThroughput = [double]$optimized.Ser
                Ratio = [double]$optimizedRatio
            })
            Write-Host ("  OptimizedProductPath: Vape={0:N0} SER={1:N0} Ratio={2:N3}" -f $optimized.Vape, $optimized.Ser, $optimizedRatio)
        }
        $selected = if ($null -ne $optimized) { $optimized } else { $apples }
        if ($null -eq $selected) {
            Write-Host "Unable to parse both-track throughput values on run $trial."
            $output | ForEach-Object { Write-Host $_ }
            exit 2
        }
    }

    if ($null -eq $selected) {
        Write-Host "Unable to parse aggregated throughput values on run $trial."
        $output | ForEach-Object { Write-Host $_ }
        exit 2
    }

    $vape = [double]$selected.Vape
    $ser = [double]$selected.Ser
    $ratio = if ($ser -gt 0) { $vape / $ser } else { [double]::PositiveInfinity }

    $results.Add([pscustomobject]@{
        Run = $trial
        VapeThroughput = $vape
        SerThroughput = $ser
        Ratio = [Math]::Round($ratio, 3)
    })

    Write-Host ("  Vape={0:N0} SER={1:N0} Ratio={2:N3}" -f $vape, $ser, $ratio)

    if ($PauseBetweenTrialsMs -gt 0 -and $trial -lt $Trials) {
        Start-Sleep -Milliseconds $PauseBetweenTrialsMs
    }
}

$vapeMedian = Get-Median -values @($results | ForEach-Object { [double]$_.VapeThroughput })
$serMedian = Get-Median -values @($results | ForEach-Object { [double]$_.SerThroughput })
$ratioMedian = if ($serMedian -gt 0) { $vapeMedian / $serMedian } else { [double]::PositiveInfinity }
$vapeValues = @($results | ForEach-Object { [double]$_.VapeThroughput })
$serValues = @($results | ForEach-Object { [double]$_.SerThroughput })
$ratioValues = @($results | ForEach-Object { [double]$_.Ratio })
$vapeCov = if ($vapeMedian -ne 0) { (Get-StdDev -values $vapeValues) / $vapeMedian } else { 0.0 }
$serCov = if ($serMedian -ne 0) { (Get-StdDev -values $serValues) / $serMedian } else { 0.0 }
$ratioCov = if ($ratioMedian -ne 0) { (Get-StdDev -values $ratioValues) / $ratioMedian } else { 0.0 }
$ratioMin = ($ratioValues | Measure-Object -Minimum).Minimum
$ratioMax = ($ratioValues | Measure-Object -Maximum).Maximum

Write-Host ""
Write-Host "Results:"
$results | Format-Table Run, VapeThroughput, SerThroughput, Ratio -AutoSize
Write-Host ("Median Vape throughput: {0:N0} shoppers/sec" -f $vapeMedian)
Write-Host ("Median SER throughput:  {0:N0} shoppers/sec" -f $serMedian)
Write-Host ("Median Ratio (Vape/SER): {0:N3}" -f $ratioMedian)
Write-Host ("Stability (CoV): Vape={0:P1} SER={1:P1} Ratio={2:P1}" -f $vapeCov, $serCov, $ratioCov)
Write-Host ("Ratio spread: {0:N3} .. {1:N3}" -f $ratioMin, $ratioMax)

if ($Track -eq "both") {
    $trackSummaries = New-Object System.Collections.Generic.List[object]
    foreach ($trackName in @("ApplesToApples", "OptimizedProductPath")) {
        $summary = Get-TrackSummary -trackName $trackName -rows $trackResults[$trackName]
        if ($null -ne $summary) {
            $trackSummaries.Add($summary)
        }
    }

    if ($trackSummaries.Count -gt 0) {
        Write-Host ""
        Write-Host "Track summary (vs SER):"
        $trackSummaries | Format-Table Track, Trials, VapeMedian, SerMedian, MedianRatio, RatioOfMedians, RatioCoV, RatioSpread -AutoSize
        Write-Host "Reporting guidance: OptimizedProductPath = hot-path comparison, ApplesToApples = parity/fallback behavior."
    }
}

if ($vapeCov -gt 0.08 -or $serCov -gt 0.08 -or $ratioCov -gt 0.08) {
    Write-Warning ("Environment appears noisy (CoV > 8%). Re-run on an isolated Redis host and quiet client machine for stable comparisons.")
}

if ($ratioMedian -lt $FailBelowRatio) {
    Write-Error ("FAIL: median Vape/SER ratio {0:N3} is below threshold {1:N3}" -f $ratioMedian, $FailBelowRatio)
    exit 1
}

Write-Host ("PASS: median Vape/SER ratio {0:N3} meets threshold {1:N3}" -f $ratioMedian, $FailBelowRatio)
exit 0
