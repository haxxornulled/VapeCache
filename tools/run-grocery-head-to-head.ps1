param(
    [int]$Trials = 5,
    [int]$ShopperCount = 50000,
    [int]$MaxCartSize = 40,
    [int]$MaxDegree = 0,
    [ValidateSet("optimized", "apples", "both")]
    [string]$Track = "both",
    [ValidateSet("raw", "hybrid")]
    [string]$VapeExecutorMode = "raw",
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
    [string]$MuxEnableSpillPressureSignals = "true",
    [int]$MuxSpillFilesThreshold = 4000,
    [int]$MuxSpillActiveShardsThreshold = 48,
    [double]$MuxSpillImbalanceRatioThreshold = 1.75,
    [int]$MuxSpillSustainedWindowSeconds = 20,
    [ValidateSet("true", "false")]
    [string]$EnableDiskSpill = "false",
    [int]$SpillThresholdBytes = 262144,
    [string]$SpillDirectory = "",
    [int]$SpillPrimeRecords = 0,
    [int]$SpillPrimePayloadBytes = 65536,
    [ValidateSet("true", "false")]
    [string]$HybridFastPath = "true",
    [ValidateSet("true", "false")]
    [string]$HybridAdmissionGate = "false",
    [int]$HybridAdmissionLimit = 10,
    [int]$HybridAdmissionWaitMs = 2,
    [ValidateSet("true", "false")]
    [string]$HybridMirrorWrites = "false",
    [ValidateSet("true", "false")]
    [string]$HybridWarmReadFallback = "false",
    [ValidateSet("true", "false")]
    [string]$HybridRemoveStaleFallbackOnMiss = "false",
    [ValidateSet("true", "false")]
    [string]$CleanupRunKeys = "false",
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
    [int]$MaxRunRetries = 0,
    [int]$RetryDelaySeconds = 5,
    [int]$ChildRunTimeoutSeconds = 900,
    [int]$ProgressHeartbeatSeconds = 15,
    [switch]$StreamChildOutput,
    [ValidateSet("Trace", "Debug", "Information", "Warning", "Error", "Critical", "None")]
    [string]$BenchLogLevel = "Debug",
    [ValidateSet("true", "false")]
    [string]$GroceryVerbose = "true",
    [ValidateSet("gold-standard", "moduleless-safe")]
    [string]$WorkloadProfile = "gold-standard",
    [double]$FailBelowRatio = 1.0,
    [switch]$DisableTrackDefaults,
    [switch]$EnforceMetricGates,
    [double]$MaxP50Ratio = 1.25,
    [double]$MaxP95Ratio = 1.30,
    [double]$MaxP99Ratio = 1.35,
    [double]$MaxAllocRatio = 1.40,
    [string]$SummaryJsonPath = ""
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

if ($MuxSpillFilesThreshold -le 0) {
    throw "MuxSpillFilesThreshold must be greater than zero."
}

if ($MuxSpillActiveShardsThreshold -le 0) {
    throw "MuxSpillActiveShardsThreshold must be greater than zero."
}

if ($MuxSpillImbalanceRatioThreshold -le 0) {
    throw "MuxSpillImbalanceRatioThreshold must be greater than zero."
}

if ($MuxSpillSustainedWindowSeconds -le 0) {
    throw "MuxSpillSustainedWindowSeconds must be greater than zero."
}

if ($SpillThresholdBytes -le 0) {
    throw "SpillThresholdBytes must be greater than zero."
}

if ($SpillPrimeRecords -lt 0) {
    throw "SpillPrimeRecords cannot be negative."
}

if ($SpillPrimePayloadBytes -le 0) {
    throw "SpillPrimePayloadBytes must be greater than zero."
}

if ($HybridAdmissionLimit -lt 0) {
    throw "HybridAdmissionLimit cannot be negative."
}

if ($HybridAdmissionWaitMs -lt 0) {
    throw "HybridAdmissionWaitMs cannot be negative."
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

if ($MaxRunRetries -lt 0) {
    throw "MaxRunRetries cannot be negative."
}

if ($RetryDelaySeconds -lt 0) {
    throw "RetryDelaySeconds cannot be negative."
}

if ($ChildRunTimeoutSeconds -le 0) {
    throw "ChildRunTimeoutSeconds must be greater than zero."
}

if ($ProgressHeartbeatSeconds -lt 0) {
    throw "ProgressHeartbeatSeconds cannot be negative."
}

if ($PauseBetweenTrialsMs -lt 0) {
    throw "PauseBetweenTrialsMs cannot be negative."
}

if ($MaxP50Ratio -le 0 -or $MaxP95Ratio -le 0 -or $MaxP99Ratio -le 0 -or $MaxAllocRatio -le 0) {
    throw "Metric gate ratios must be greater than zero."
}

$hasMaxDegreeOverride = $PSBoundParameters.ContainsKey("MaxDegree")
$hasMuxConnectionsOverride = $PSBoundParameters.ContainsKey("MuxConnections")
$hasMuxAdaptiveCoalescingOverride = $PSBoundParameters.ContainsKey("MuxAdaptiveCoalescing")
$hasMuxSpillFilesThresholdOverride = $PSBoundParameters.ContainsKey("MuxSpillFilesThreshold")
$hasMuxSpillActiveShardsThresholdOverride = $PSBoundParameters.ContainsKey("MuxSpillActiveShardsThreshold")
$hasMuxSpillImbalanceRatioThresholdOverride = $PSBoundParameters.ContainsKey("MuxSpillImbalanceRatioThreshold")
$hasMuxSpillSustainedWindowSecondsOverride = $PSBoundParameters.ContainsKey("MuxSpillSustainedWindowSeconds")
$hasHybridAdmissionLimitOverride = $PSBoundParameters.ContainsKey("HybridAdmissionLimit")

if ($ServerGc -eq "true") {
    $env:DOTNET_GCServer = "1"
}
elseif ($ServerGc -eq "false") {
    $env:DOTNET_GCServer = "0"
}

function Test-IsLocalRedisHost([string]$HostValue) {
    if ([string]::IsNullOrWhiteSpace($HostValue)) {
        return $false
    }

    $normalized = $HostValue.Trim().ToLowerInvariant()
    if ($normalized -eq "localhost" -or $normalized -eq "127.0.0.1" -or $normalized -eq "::1") {
        return $true
    }

    try {
        $ip = [System.Net.IPAddress]::Parse($normalized)
        return [System.Net.IPAddress]::IsLoopback($ip)
    }
    catch {
        return $false
    }
}

function Get-ConnectionStringAuthMetadata([string]$Value) {
    $result = [pscustomobject]@{
        IsRemote = $true
        HasUsername = $false
        HasPassword = $false
    }

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $result
    }

    $uri = $null
    if ([Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri)) {
        $result.IsRemote = -not (Test-IsLocalRedisHost -HostValue $uri.Host)
        if (-not [string]::IsNullOrWhiteSpace($uri.UserInfo)) {
            $parts = $uri.UserInfo.Split(":", 2, [System.StringSplitOptions]::None)
            if ($parts.Length -gt 0 -and -not [string]::IsNullOrWhiteSpace($parts[0])) {
                $result.HasUsername = $true
            }
            if ($parts.Length -gt 1 -and -not [string]::IsNullOrWhiteSpace($parts[1])) {
                $result.HasPassword = $true
            }
        }

        return $result
    }

    $segments = $Value.Split(",", [System.StringSplitOptions]::RemoveEmptyEntries)
    if ($segments.Length -gt 0) {
        $hostToken = $segments[0]
        $hostName = $hostToken
        if ($hostToken.Contains("=")) {
            $hostName = ($hostToken.Split("=", 2, [System.StringSplitOptions]::None)[1]).Trim()
        }
        if ($hostName.Contains(":")) {
            $hostName = $hostName.Split(":", 2, [System.StringSplitOptions]::None)[0]
        }

        $result.IsRemote = -not (Test-IsLocalRedisHost -HostValue $hostName)
    }

    foreach ($segment in $segments) {
        $parts = $segment.Split("=", 2, [System.StringSplitOptions]::None)
        if ($parts.Length -ne 2) {
            continue
        }

        $key = $parts[0].Trim().ToLowerInvariant()
        $val = $parts[1].Trim()
        if ($key -eq "user" -or $key -eq "username") {
            $result.HasUsername = -not [string]::IsNullOrWhiteSpace($val)
        }
        elseif ($key -eq "password" -or $key -eq "pwd") {
            $result.HasPassword = -not [string]::IsNullOrWhiteSpace($val)
        }
    }

    return $result
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

if ($useConnectionString) {
    $authMetadata = Get-ConnectionStringAuthMetadata -Value $RedisConnectionString
    if ($authMetadata.IsRemote -and (-not $authMetadata.HasUsername -or -not $authMetadata.HasPassword)) {
        throw "Remote Redis connection strings must include ACL username and password. Provide credentials in -RedisConnectionString."
    }
}
elseif (-not (Test-IsLocalRedisHost -HostValue $RedisHost)) {
    if ([string]::IsNullOrWhiteSpace($RedisUsername) -or [string]::IsNullOrWhiteSpace($RedisPassword)) {
        throw "Remote Redis endpoints require ACL authentication. Provide -RedisUsername and -RedisPassword."
    }
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
    $effectiveEnableSpillPressureSignals = $MuxEnableSpillPressureSignals.ToLowerInvariant()
    $effectiveSpillFilesThreshold = $MuxSpillFilesThreshold
    $effectiveSpillActiveShardsThreshold = $MuxSpillActiveShardsThreshold
    $effectiveSpillImbalanceRatioThreshold = $MuxSpillImbalanceRatioThreshold
    $effectiveSpillSustainedWindowSeconds = $MuxSpillSustainedWindowSeconds

    if ($DisableTrackDefaults) {
        if (-not $hasMuxConnectionsOverride) {
            # Strict/fair baseline: single connection avoids queue fan-out jitter and improves parity.
            $effectiveConnections = 1
        }

        if (-not $hasMuxAdaptiveCoalescingOverride) {
            # Strict/fair baseline: disable adaptive coalescing for tighter tail behavior.
            $effectiveAdaptive = "false"
        }
    }
    elseif ($RunTrack -eq "apples") {
        if (-not $hasMuxConnectionsOverride) {
            # Apples-to-apples workload favors fewer connections to reduce fan-out overhead.
            $effectiveConnections = 1
        }

        if (-not $hasMuxAdaptiveCoalescingOverride) {
            # Adaptive coalescing can add burst jitter on parity workloads.
            $effectiveAdaptive = "false"
        }

        if (-not $hasMuxSpillFilesThresholdOverride) {
            # Parity path should scale mostly from mux pressure, not transient spill churn.
            $effectiveSpillFilesThreshold = 10000
        }
        if (-not $hasMuxSpillActiveShardsThresholdOverride) {
            $effectiveSpillActiveShardsThreshold = 128
        }
        if (-not $hasMuxSpillImbalanceRatioThresholdOverride) {
            $effectiveSpillImbalanceRatioThreshold = 2.20
        }
        if (-not $hasMuxSpillSustainedWindowSecondsOverride) {
            $effectiveSpillSustainedWindowSeconds = 45
        }
    }
    elseif ($RunTrack -eq "optimized") {
        if (-not $hasMuxConnectionsOverride) {
            # Optimized path sustains the best Vape/SER ratio with a single fast lane in this workload.
            $effectiveConnections = 1
        }

        if (-not $hasMuxAdaptiveCoalescingOverride) {
            # Keep optimized tail latency tight by avoiding adaptive burst jitter.
            $effectiveAdaptive = "false"
        }

        if (-not $hasMuxSpillFilesThresholdOverride) {
            # Optimized path gets faster reaction to sustained spill growth.
            $effectiveSpillFilesThreshold = 4000
        }
        if (-not $hasMuxSpillActiveShardsThresholdOverride) {
            $effectiveSpillActiveShardsThreshold = 48
        }
        if (-not $hasMuxSpillImbalanceRatioThresholdOverride) {
            $effectiveSpillImbalanceRatioThreshold = 1.75
        }
        if (-not $hasMuxSpillSustainedWindowSecondsOverride) {
            $effectiveSpillSustainedWindowSeconds = 20
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
        EnableSpillPressureSignals = $effectiveEnableSpillPressureSignals
        SpillFilesThreshold = $effectiveSpillFilesThreshold
        SpillActiveShardsThreshold = $effectiveSpillActiveShardsThreshold
        SpillImbalanceRatioThreshold = $effectiveSpillImbalanceRatioThreshold
        SpillSustainedWindowSeconds = $effectiveSpillSustainedWindowSeconds
    }
}

function Get-EffectiveMaxDegree([string]$RunTrack) {
    if ($hasMaxDegreeOverride) {
        if ($MaxDegree -gt 0) {
            return $MaxDegree
        }

        return $null
    }

    if ($DisableTrackDefaults) {
        # Strict/fair baseline used when track defaults are intentionally disabled.
        return 6
    }

    if ($RunTrack -eq "apples") {
        # Keep apples runs in the fairness window where both clients stay CPU/network balanced.
        return 6
    }
    if ($RunTrack -eq "optimized") {
        # Optimized path holds a stronger relative lead at this worker pressure.
        return 10
    }

    if ($MaxDegree -gt 0) {
        return $MaxDegree
    }

    return $null
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
    $env:VAPECACHE_BENCH_ENABLE_SPILL_PRESSURE_SIGNALS = "$($Settings.EnableSpillPressureSignals)"
    $env:VAPECACHE_BENCH_SPILL_FILES_THRESHOLD = "$($Settings.SpillFilesThreshold)"
    $env:VAPECACHE_BENCH_SPILL_ACTIVE_SHARDS_THRESHOLD = "$($Settings.SpillActiveShardsThreshold)"
    $env:VAPECACHE_BENCH_SPILL_IMBALANCE_RATIO_THRESHOLD = "$($Settings.SpillImbalanceRatioThreshold)"
    $env:VAPECACHE_BENCH_SPILL_SUSTAINED_WINDOW_SECONDS = "$($Settings.SpillSustainedWindowSeconds)"
}

function Get-EffectiveHybridAdmissionLimit([string]$RunTrack) {
    if ($hasHybridAdmissionLimitOverride) {
        return [Math]::Max(0, $HybridAdmissionLimit)
    }

    if (-not $DisableTrackDefaults -and $VapeExecutorMode -eq "hybrid") {
        if ($RunTrack -eq "apples") {
            # Apples maintains parity best with slightly higher admission headroom.
            return 12
        }
        if ($RunTrack -eq "optimized") {
            # Optimized track keeps top-end while protecting against degree-12 collapse.
            return 10
        }
    }

    return [Math]::Max(0, $HybridAdmissionLimit)
}

function Set-HybridEnvironment([string]$RunTrack) {
    $env:VAPECACHE_BENCH_HYBRID_FAST_PATH = $HybridFastPath.ToLowerInvariant()
    $env:VAPECACHE_BENCH_HYBRID_ADMISSION_GATE = $HybridAdmissionGate.ToLowerInvariant()
    $effectiveLimit = Get-EffectiveHybridAdmissionLimit -RunTrack $RunTrack
    $env:VAPECACHE_BENCH_HYBRID_ADMISSION_LIMIT = "$effectiveLimit"
    $env:VAPECACHE_BENCH_HYBRID_ADMISSION_WAIT_MS = "$HybridAdmissionWaitMs"
    $env:VAPECACHE_BENCH_HYBRID_MIRROR_WRITES = $HybridMirrorWrites.ToLowerInvariant()
    $env:VAPECACHE_BENCH_HYBRID_WARM_READ_FALLBACK = $HybridWarmReadFallback.ToLowerInvariant()
    $env:VAPECACHE_BENCH_HYBRID_REMOVE_STALE_FALLBACK_ON_MISS = $HybridRemoveStaleFallbackOnMiss.ToLowerInvariant()
    return $effectiveLimit
}

$env:VAPECACHE_RUN_COMPARISON = "true"
$env:VAPECACHE_BENCH_SHOPPERS = "$ShopperCount"
$env:VAPECACHE_MAX_CART_SIZE = "$MaxCartSize"
$env:VAPECACHE_BENCH_TRACK = $Track
$env:VAPECACHE_BENCH_VAPE_EXECUTOR_MODE = $VapeExecutorMode
$env:VAPECACHE_BENCH_CLEANUP_RUN_KEYS = $CleanupRunKeys.ToLowerInvariant()
$env:VAPECACHE_BENCH_LOG_LEVEL = $BenchLogLevel
$env:VAPECACHE_GROCERYSTORE_VERBOSE = $GroceryVerbose.ToLowerInvariant()

switch ($WorkloadProfile) {
    "gold-standard" {
        $env:VAPECACHE_BENCH_ENABLE_COMMAND_COVERAGE = "true"
        $env:VAPECACHE_BENCH_ENABLE_TAG_INVALIDATION = "true"
        $env:VAPECACHE_BENCH_ENABLE_CHECKOUT_RECEIPT_FLOW = "true"
    }
    "moduleless-safe" {
        $env:VAPECACHE_BENCH_ENABLE_COMMAND_COVERAGE = "false"
        $env:VAPECACHE_BENCH_ENABLE_TAG_INVALIDATION = "true"
        $env:VAPECACHE_BENCH_ENABLE_CHECKOUT_RECEIPT_FLOW = "false"
    }
}

$env:VAPECACHE_BENCH_ENABLE_DISK_SPILL = $EnableDiskSpill.ToLowerInvariant()
$env:VAPECACHE_BENCH_SPILL_THRESHOLD_BYTES = "$SpillThresholdBytes"
$env:VAPECACHE_BENCH_SPILL_PRIME_RECORDS = "$SpillPrimeRecords"
$env:VAPECACHE_BENCH_SPILL_PRIME_PAYLOAD_BYTES = "$SpillPrimePayloadBytes"
$env:VAPECACHE_BENCH_SPILL_DIRECTORY = $SpillDirectory
if ($Track -eq "both") {
    Set-HybridEnvironment -RunTrack "optimized" | Out-Null
}
else {
    Set-HybridEnvironment -RunTrack $Track | Out-Null
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
Write-Host "Parse retries per run: $MaxRunRetries (delay ${RetryDelaySeconds}s)"
Write-Host "Child timeout per run: ${ChildRunTimeoutSeconds}s"
$heartbeatLabel = if ($ProgressHeartbeatSeconds -gt 0) { "${ProgressHeartbeatSeconds}s" } else { "off" }
Write-Host "Observability: Heartbeat=$heartbeatLabel StreamChildOutput=$($StreamChildOutput.IsPresent)"
Write-Host "Shoppers: $ShopperCount"
Write-Host "Max cart size: $MaxCartSize"
Write-Host "Track: $Track"
# Mystery Inc. mode: split tracks help us compare clues before saying "Ruh-roh" about regressions.
Write-Host "Vape executor mode: $VapeExecutorMode"
Write-Host "Workload profile: $WorkloadProfile"
if ($Track -eq "both") {
    Write-Host "Both-track isolation: enabled"
}
Write-Host "Track defaults: $(if ($DisableTrackDefaults) { "disabled" } else { "enabled" })"
if ($Track -eq "both") {
    $applesMaxDegree = Get-EffectiveMaxDegree -RunTrack "apples"
    $optimizedMaxDegree = Get-EffectiveMaxDegree -RunTrack "optimized"
    Write-Host "Max degree (apples): $(if ($null -ne $applesMaxDegree) { "$applesMaxDegree" } else { "auto" })"
    Write-Host "Max degree (optimized): $(if ($null -ne $optimizedMaxDegree) { "$optimizedMaxDegree" } else { "auto" })"
    $applesMux = Get-EffectiveMuxSettings -RunTrack "apples"
    $optimizedMux = Get-EffectiveMuxSettings -RunTrack "optimized"
    $applesHybridLimit = Get-EffectiveHybridAdmissionLimit -RunTrack "apples"
    $optimizedHybridLimit = Get-EffectiveHybridAdmissionLimit -RunTrack "optimized"
    Write-Host "Mux (apples): Profile=$($applesMux.Profile) Connections=$($applesMux.Connections) InFlight=$($applesMux.InFlight) Coalesce=$($applesMux.Coalesce) Adaptive=$($applesMux.Adaptive) SocketReader=$($applesMux.SocketReader) DedicatedWorkers=$($applesMux.DedicatedWorkers) TimeoutMs=$($applesMux.ResponseTimeoutMs) SpillSignals=$($applesMux.EnableSpillPressureSignals) SpillFiles=$($applesMux.SpillFilesThreshold) SpillShards=$($applesMux.SpillActiveShardsThreshold) SpillImbalance=$([string]::Format([System.Globalization.CultureInfo]::InvariantCulture, '{0:0.##}', $applesMux.SpillImbalanceRatioThreshold)) SpillWindowSec=$($applesMux.SpillSustainedWindowSeconds)"
    Write-Host "Mux (optimized): Profile=$($optimizedMux.Profile) Connections=$($optimizedMux.Connections) InFlight=$($optimizedMux.InFlight) Coalesce=$($optimizedMux.Coalesce) Adaptive=$($optimizedMux.Adaptive) SocketReader=$($optimizedMux.SocketReader) DedicatedWorkers=$($optimizedMux.DedicatedWorkers) TimeoutMs=$($optimizedMux.ResponseTimeoutMs) SpillSignals=$($optimizedMux.EnableSpillPressureSignals) SpillFiles=$($optimizedMux.SpillFilesThreshold) SpillShards=$($optimizedMux.SpillActiveShardsThreshold) SpillImbalance=$([string]::Format([System.Globalization.CultureInfo]::InvariantCulture, '{0:0.##}', $optimizedMux.SpillImbalanceRatioThreshold)) SpillWindowSec=$($optimizedMux.SpillSustainedWindowSeconds)"
    Write-Host "Hybrid admission limit (apples): $applesHybridLimit"
    Write-Host "Hybrid admission limit (optimized): $optimizedHybridLimit"
}
else {
    $selectedMaxDegree = Get-EffectiveMaxDegree -RunTrack $Track
    $maxDegreeSuffix = ""
    if ($null -ne $selectedMaxDegree) {
        if ($hasMaxDegreeOverride) {
            $maxDegreeSuffix = " (override)"
        }
        elseif (-not $DisableTrackDefaults -and $Track -eq "apples") {
            $maxDegreeSuffix = " (apples default)"
        }
    }
    Write-Host "Max degree: $(if ($null -ne $selectedMaxDegree) { "$selectedMaxDegree$maxDegreeSuffix" } else { "auto" })"
    $selectedMux = Get-EffectiveMuxSettings -RunTrack $Track
    $selectedHybridLimit = Set-HybridEnvironment -RunTrack $Track
    Set-MuxEnvironment -Settings $selectedMux
    if ($null -ne $selectedMaxDegree) {
        $env:VAPECACHE_BENCH_MAX_DEGREE = "$selectedMaxDegree"
    }
    else {
        Remove-Item Env:VAPECACHE_BENCH_MAX_DEGREE -ErrorAction SilentlyContinue
    }
    Write-Host "Mux: Profile=$($selectedMux.Profile) Connections=$($selectedMux.Connections) InFlight=$($selectedMux.InFlight) Coalesce=$($selectedMux.Coalesce) Adaptive=$($selectedMux.Adaptive) SocketReader=$($selectedMux.SocketReader) DedicatedWorkers=$($selectedMux.DedicatedWorkers) TimeoutMs=$($selectedMux.ResponseTimeoutMs) SpillSignals=$($selectedMux.EnableSpillPressureSignals) SpillFiles=$($selectedMux.SpillFilesThreshold) SpillShards=$($selectedMux.SpillActiveShardsThreshold) SpillImbalance=$([string]::Format([System.Globalization.CultureInfo]::InvariantCulture, '{0:0.##}', $selectedMux.SpillImbalanceRatioThreshold)) SpillWindowSec=$($selectedMux.SpillSustainedWindowSeconds)"
    Write-Host "Hybrid admission limit: $selectedHybridLimit"
}
Write-Host "Cleanup: RunKeys=$($env:VAPECACHE_BENCH_CLEANUP_RUN_KEYS)"
if ($ShopperCount -ge 500000 -and $CleanupRunKeys -eq "false") {
    Write-Warning "Large runs with CleanupRunKeys=false can force Redis memory churn/reloads (LOADING) and invalidate long soak runs."
    Write-Warning "For stability soak campaigns, prefer -CleanupRunKeys true."
}
Write-Host "Hybrid: FastPath=$($env:VAPECACHE_BENCH_HYBRID_FAST_PATH) AdmissionGate=$($env:VAPECACHE_BENCH_HYBRID_ADMISSION_GATE) AdmissionLimit=$($env:VAPECACHE_BENCH_HYBRID_ADMISSION_LIMIT) AdmissionWaitMs=$($env:VAPECACHE_BENCH_HYBRID_ADMISSION_WAIT_MS) MirrorWrites=$($env:VAPECACHE_BENCH_HYBRID_MIRROR_WRITES) WarmReadFallback=$($env:VAPECACHE_BENCH_HYBRID_WARM_READ_FALLBACK) RemoveStaleFallbackOnMiss=$($env:VAPECACHE_BENCH_HYBRID_REMOVE_STALE_FALLBACK_ON_MISS)"
Write-Host "Phases: CommandCoverage=$($env:VAPECACHE_BENCH_ENABLE_COMMAND_COVERAGE) TagInvalidation=$($env:VAPECACHE_BENCH_ENABLE_TAG_INVALIDATION) CheckoutReceiptFlow=$($env:VAPECACHE_BENCH_ENABLE_CHECKOUT_RECEIPT_FLOW)"
Write-Host "Spill: EnableDiskSpill=$($env:VAPECACHE_BENCH_ENABLE_DISK_SPILL) ThresholdBytes=$($env:VAPECACHE_BENCH_SPILL_THRESHOLD_BYTES) PrimeRecords=$($env:VAPECACHE_BENCH_SPILL_PRIME_RECORDS) PrimePayloadBytes=$($env:VAPECACHE_BENCH_SPILL_PRIME_PAYLOAD_BYTES) Directory=$(if ([string]::IsNullOrWhiteSpace($env:VAPECACHE_BENCH_SPILL_DIRECTORY)) { "<default>" } else { $env:VAPECACHE_BENCH_SPILL_DIRECTORY })"
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

function Get-GeometricMean([double[]]$values) {
    if ($values.Count -eq 0) {
        return [double]::NaN
    }

    $sumLog = 0.0
    foreach ($value in $values) {
        if ($value -le 0 -or [double]::IsNaN($value) -or [double]::IsInfinity($value)) {
            return [double]::NaN
        }
        $sumLog += [Math]::Log($value)
    }

    return [Math]::Exp($sumLog / $values.Count)
}

function Get-TrackSummary([string]$trackName, [System.Collections.Generic.List[object]]$rows) {
    if ($rows.Count -eq 0) {
        return $null
    }

    $vapeValues = @($rows | ForEach-Object { [double]$_.VapeThroughput })
    $serValues = @($rows | ForEach-Object { [double]$_.SerThroughput })
    $throughputRatioValues = @($rows | ForEach-Object { [double]$_.ThroughputRatio })
    $p50RatioValues = @($rows | ForEach-Object { [double]$_.P50Ratio })
    $p95RatioValues = @($rows | ForEach-Object { [double]$_.P95Ratio })
    $p99RatioValues = @($rows | ForEach-Object { [double]$_.P99Ratio })
    $allocRatioValues = @($rows | ForEach-Object { [double]$_.AllocRatio })
    $vapeMedian = Get-Median -values $vapeValues
    $serMedian = Get-Median -values $serValues
    $ratioOfMedians = if ($serMedian -gt 0) { $vapeMedian / $serMedian } else { [double]::PositiveInfinity }
    $throughputRatioMedian = Get-Median -values $throughputRatioValues
    $throughputRatioCov = if ($throughputRatioMedian -ne 0) { (Get-StdDev -values $throughputRatioValues) / $throughputRatioMedian } else { 0.0 }
    $throughputRatioMin = ($throughputRatioValues | Measure-Object -Minimum).Minimum
    $throughputRatioMax = ($throughputRatioValues | Measure-Object -Maximum).Maximum
    $p50RatioMedian = Get-Median -values $p50RatioValues
    $p95RatioMedian = Get-Median -values $p95RatioValues
    $p99RatioMedian = Get-Median -values $p99RatioValues
    $allocRatioMedian = Get-Median -values $allocRatioValues

    return [pscustomobject]@{
        Track = $trackName
        Trials = $rows.Count
        VapeMedian = [Math]::Round($vapeMedian, 0)
        SerMedian = [Math]::Round($serMedian, 0)
        ThroughputRatioMedian = [Math]::Round($throughputRatioMedian, 3)
        ThroughputRatioOfMedians = [Math]::Round($ratioOfMedians, 3)
        ThroughputRatioCoV = [Math]::Round($throughputRatioCov * 100.0, 1)
        ThroughputRatioSpread = ("{0:N3} .. {1:N3}" -f $throughputRatioMin, $throughputRatioMax)
        P50RatioMedian = [Math]::Round($p50RatioMedian, 3)
        P95RatioMedian = [Math]::Round($p95RatioMedian, 3)
        P99RatioMedian = [Math]::Round($p99RatioMedian, 3)
        AllocRatioMedian = [Math]::Round($allocRatioMedian, 3)
    }
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

function Parse-MetricOrNaN([hashtable]$map, [string]$key) {
    if (-not $map.ContainsKey($key)) {
        return [double]::NaN
    }

    $value = 0.0
    $ok = [double]::TryParse(
        $map[$key],
        [System.Globalization.NumberStyles]::Float,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [ref]$value)
    if (-not $ok) {
        return [double]::NaN
    }

    return $value
}

function Parse-BenchmarkOutput([object[]]$OutputLines) {
    $throughputPattern = '^Throughput \(shoppers/sec\)\s+([0-9,]+(?:\.[0-9]+)?)\s+([0-9,]+(?:\.[0-9]+)?)\b'
    $trackPattern = '^Track:\s*(.+)$'
    $parsedByTrack = @{}
    $providersByTrack = @{}
    $currentTrack = "single"

    foreach ($line in $OutputLines) {
        $text = "$line"
        $trackMatch = [regex]::Match($text, $trackPattern)
        if ($trackMatch.Success) {
            $currentTrack = $trackMatch.Groups[1].Value.Trim()
            continue
        }

        if ($text -like "RESULT|*") {
            $result = Parse-ResultLine -line $text
            $provider = if ($result.ContainsKey("Provider")) { $result["Provider"] } else { "" }
            if ([string]::IsNullOrWhiteSpace($provider)) {
                continue
            }

            $track = if ($result.ContainsKey("Track")) { $result["Track"] } else { $currentTrack }
            if ([string]::IsNullOrWhiteSpace($track)) {
                $track = "single"
            }

            if (-not $providersByTrack.ContainsKey($track)) {
                $providersByTrack[$track] = @{}
            }

            $providersByTrack[$track][$provider] = [pscustomobject]@{
                Throughput = Parse-MetricOrNaN -map $result -key "Throughput"
                P50Ms = Parse-MetricOrNaN -map $result -key "P50Ms"
                P95Ms = Parse-MetricOrNaN -map $result -key "P95Ms"
                P99Ms = Parse-MetricOrNaN -map $result -key "P99Ms"
                AllocBytesPerShopper = Parse-MetricOrNaN -map $result -key "AllocBytesPerShopper"
            }
            continue
        }

        $throughputMatch = [regex]::Match($text, $throughputPattern)
        if (-not $throughputMatch.Success) {
            continue
        }

        $parsedByTrack[$currentTrack] = [pscustomobject]@{
            VapeThroughput = [double](($throughputMatch.Groups[1].Value) -replace ",", "")
            SerThroughput = [double](($throughputMatch.Groups[2].Value) -replace ",", "")
            VapeP50Ms = [double]::NaN
            SerP50Ms = [double]::NaN
            VapeP95Ms = [double]::NaN
            SerP95Ms = [double]::NaN
            VapeP99Ms = [double]::NaN
            SerP99Ms = [double]::NaN
            VapeAllocBytesPerShopper = [double]::NaN
            SerAllocBytesPerShopper = [double]::NaN
        }
    }

    foreach ($trackKey in $providersByTrack.Keys) {
        $providerMap = $providersByTrack[$trackKey]
        $vapeProvider = $providerMap.Keys | Where-Object { $_ -like "VapeCache*" } | Select-Object -First 1
        $serProvider = $providerMap.Keys | Where-Object { $_ -like "StackExchange.Redis*" } | Select-Object -First 1
        if ([string]::IsNullOrWhiteSpace($vapeProvider) -or [string]::IsNullOrWhiteSpace($serProvider)) {
            continue
        }

        $vape = $providerMap[$vapeProvider]
        $ser = $providerMap[$serProvider]
        $parsedByTrack[$trackKey] = [pscustomobject]@{
            VapeThroughput = [double]$vape.Throughput
            SerThroughput = [double]$ser.Throughput
            VapeP50Ms = [double]$vape.P50Ms
            SerP50Ms = [double]$ser.P50Ms
            VapeP95Ms = [double]$vape.P95Ms
            SerP95Ms = [double]$ser.P95Ms
            VapeP99Ms = [double]$vape.P99Ms
            SerP99Ms = [double]$ser.P99Ms
            VapeAllocBytesPerShopper = [double]$vape.AllocBytesPerShopper
            SerAllocBytesPerShopper = [double]$ser.AllocBytesPerShopper
        }
    }

    return $parsedByTrack
}

function New-TrialRow([int]$Run, [pscustomobject]$Metrics) {
    $vapeThroughput = [double]$Metrics.VapeThroughput
    $serThroughput = [double]$Metrics.SerThroughput
    $vapeP50Ms = [double]$Metrics.VapeP50Ms
    $serP50Ms = [double]$Metrics.SerP50Ms
    $vapeP95Ms = [double]$Metrics.VapeP95Ms
    $serP95Ms = [double]$Metrics.SerP95Ms
    $vapeP99Ms = [double]$Metrics.VapeP99Ms
    $serP99Ms = [double]$Metrics.SerP99Ms
    $vapeAllocPerShopper = [double]$Metrics.VapeAllocBytesPerShopper
    $serAllocPerShopper = [double]$Metrics.SerAllocBytesPerShopper

    $throughputRatio = if ($serThroughput -gt 0) { $vapeThroughput / $serThroughput } else { [double]::PositiveInfinity }
    $p50Ratio = if ($serP50Ms -gt 0) { $vapeP50Ms / $serP50Ms } else { [double]::NaN }
    $p95Ratio = if ($serP95Ms -gt 0) { $vapeP95Ms / $serP95Ms } else { [double]::NaN }
    $p99Ratio = if ($serP99Ms -gt 0) { $vapeP99Ms / $serP99Ms } else { [double]::NaN }
    $allocRatio = if ($serAllocPerShopper -gt 0) { $vapeAllocPerShopper / $serAllocPerShopper } else { [double]::NaN }

    return [pscustomobject]@{
        Run = $Run
        VapeThroughput = $vapeThroughput
        SerThroughput = $serThroughput
        ThroughputRatio = [Math]::Round($throughputRatio, 3)
        VapeP50Ms = $vapeP50Ms
        SerP50Ms = $serP50Ms
        P50Ratio = [Math]::Round($p50Ratio, 3)
        VapeP95Ms = $vapeP95Ms
        SerP95Ms = $serP95Ms
        P95Ratio = [Math]::Round($p95Ratio, 3)
        VapeP99Ms = $vapeP99Ms
        SerP99Ms = $serP99Ms
        P99Ratio = [Math]::Round($p99Ratio, 3)
        VapeAllocBytesPerShopper = $vapeAllocPerShopper
        SerAllocBytesPerShopper = $serAllocPerShopper
        AllocRatio = [Math]::Round($allocRatio, 3)
    }
}

function Get-DotnetProcessSnapshot {
    $snapshot = @{}
    $processes = @(Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction SilentlyContinue)
    foreach ($process in $processes) {
        $processId = [int]$process.ProcessId
        $snapshot[$processId] = [pscustomobject]@{
            ProcessId = $processId
            ParentProcessId = [int]$process.ParentProcessId
            CommandLine = [string]$process.CommandLine
        }
    }

    return $snapshot
}

function Get-DotnetDescendantProcessIds([hashtable]$Snapshot, [int]$RootProcessId) {
    $descendants = New-Object 'System.Collections.Generic.HashSet[int]'
    $queue = New-Object 'System.Collections.Generic.Queue[int]'
    $queue.Enqueue($RootProcessId)

    while ($queue.Count -gt 0) {
        $parentId = $queue.Dequeue()
        foreach ($entry in $Snapshot.Values) {
            if ($entry.ParentProcessId -ne $parentId) {
                continue
            }

            if ($descendants.Add($entry.ProcessId)) {
                $queue.Enqueue($entry.ProcessId)
            }
        }
    }

    return @($descendants)
}

function Get-NewVapeCacheDotnetProcessIds([hashtable]$BeforeSnapshot, [hashtable]$AfterSnapshot) {
    $newProcesses = New-Object System.Collections.Generic.List[int]
    foreach ($entry in $AfterSnapshot.Values) {
        if ($BeforeSnapshot.ContainsKey($entry.ProcessId)) {
            continue
        }

        $commandLine = if ($null -eq $entry.CommandLine) { "" } else { $entry.CommandLine }
        if ($commandLine.IndexOf("VapeCache.Console", [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
            continue
        }

        $newProcesses.Add($entry.ProcessId)
    }

    return @($newProcesses)
}

function Stop-DotnetProcesses([int[]]$ProcessIds, [string]$Reason) {
    if ($null -eq $ProcessIds -or $ProcessIds.Count -eq 0) {
        return
    }

    $uniqueIds = @($ProcessIds | Sort-Object -Unique)
    foreach ($processId in $uniqueIds) {
        try {
            Stop-Process -Id $processId -Force -ErrorAction Stop
            Write-Warning ("Stopped dotnet PID {0} ({1})." -f $processId, $Reason)
        }
        catch {
            # Ignore races where process exits before cleanup.
        }
    }
}

function Get-NewFileLines([string]$Path, [int]$AlreadyRead) {
    $result = [pscustomobject]@{
        Lines = @()
        NextIndex = $AlreadyRead
    }

    if (-not (Test-Path $Path)) {
        return $result
    }

    $allLines = @(Get-Content -Path $Path -ErrorAction SilentlyContinue)
    if ($null -eq $allLines -or $allLines.Count -le $AlreadyRead) {
        return $result
    }

    $result.Lines = @($allLines[$AlreadyRead..($allLines.Count - 1)])
    $result.NextIndex = $allLines.Count
    return $result
}

function Invoke-BenchmarkRun([string]$RunTrack) {
    $beforeSnapshot = Get-DotnetProcessSnapshot
    $hadTrack = Test-Path Env:VAPECACHE_BENCH_TRACK
    $previousTrack = $env:VAPECACHE_BENCH_TRACK
    $hadExecutorMode = Test-Path Env:VAPECACHE_BENCH_VAPE_EXECUTOR_MODE
    $previousExecutorMode = $env:VAPECACHE_BENCH_VAPE_EXECUTOR_MODE
    $hadMaxDegree = Test-Path Env:VAPECACHE_BENCH_MAX_DEGREE
    $previousMaxDegree = $env:VAPECACHE_BENCH_MAX_DEGREE
    $hadHybridLimit = Test-Path Env:VAPECACHE_BENCH_HYBRID_ADMISSION_LIMIT
    $previousHybridLimit = $env:VAPECACHE_BENCH_HYBRID_ADMISSION_LIMIT
    $hybridEnvNames = @(
        "VAPECACHE_BENCH_HYBRID_FAST_PATH",
        "VAPECACHE_BENCH_HYBRID_ADMISSION_GATE",
        "VAPECACHE_BENCH_HYBRID_ADMISSION_LIMIT",
        "VAPECACHE_BENCH_HYBRID_ADMISSION_WAIT_MS",
        "VAPECACHE_BENCH_HYBRID_MIRROR_WRITES",
        "VAPECACHE_BENCH_HYBRID_WARM_READ_FALLBACK",
        "VAPECACHE_BENCH_HYBRID_REMOVE_STALE_FALLBACK_ON_MISS"
    )
    $previousHybridEnv = @{}
    foreach ($name in $hybridEnvNames) {
        $previousHybridEnv[$name] = [Environment]::GetEnvironmentVariable($name)
    }
    $muxEnvNames = @(
        "VAPECACHE_BENCH_MUX_PROFILE",
        "VAPECACHE_BENCH_MUX_CONNECTIONS",
        "VAPECACHE_BENCH_MUX_INFLIGHT",
        "VAPECACHE_BENCH_MUX_COALESCE",
        "VAPECACHE_BENCH_MUX_ADAPTIVE_COALESCING",
        "VAPECACHE_BENCH_SOCKET_RESP_READER",
        "VAPECACHE_BENCH_DEDICATED_LANE_WORKERS",
        "VAPECACHE_BENCH_MUX_RESPONSE_TIMEOUT_MS",
        "VAPECACHE_BENCH_ENABLE_SPILL_PRESSURE_SIGNALS",
        "VAPECACHE_BENCH_SPILL_FILES_THRESHOLD",
        "VAPECACHE_BENCH_SPILL_ACTIVE_SHARDS_THRESHOLD",
        "VAPECACHE_BENCH_SPILL_IMBALANCE_RATIO_THRESHOLD",
        "VAPECACHE_BENCH_SPILL_SUSTAINED_WINDOW_SECONDS"
    )
    $previousMuxEnv = @{}
    foreach ($name in $muxEnvNames) {
        $previousMuxEnv[$name] = [Environment]::GetEnvironmentVariable($name)
    }
    $env:VAPECACHE_BENCH_TRACK = $RunTrack
    $env:VAPECACHE_BENCH_VAPE_EXECUTOR_MODE = $VapeExecutorMode
    $effectiveHybridLimit = Set-HybridEnvironment -RunTrack $RunTrack
    $maxDegree = Get-EffectiveMaxDegree -RunTrack $RunTrack
    if ($null -ne $maxDegree) {
        $env:VAPECACHE_BENCH_MAX_DEGREE = "$maxDegree"
    }
    else {
        Remove-Item Env:VAPECACHE_BENCH_MAX_DEGREE -ErrorAction SilentlyContinue
    }
    $settings = Get-EffectiveMuxSettings -RunTrack $RunTrack
    Set-MuxEnvironment -Settings $settings
    $previousNativeErrorPreference = $PSNativeCommandUseErrorActionPreference
    $stdoutPath = [System.IO.Path]::GetTempFileName()
    $stderrPath = [System.IO.Path]::GetTempFileName()
    $runOutput = @()
    $stdoutReadIndex = 0
    $stderrReadIndex = 0
    $child = $null
    try {
        Write-Host ("  [TrackConfig] {0}: MaxDegree={1} Profile={2} Connections={3} InFlight={4} Coalesce={5} Adaptive={6} SocketReader={7} DedicatedWorkers={8} TimeoutMs={9} SpillSignals={10} SpillFiles={11} SpillShards={12} SpillImbalance={13} SpillWindowSec={14} HybridAdmissionLimit={15}" -f $RunTrack, $(if ($null -ne $maxDegree) { $maxDegree } else { "auto" }), $settings.Profile, $settings.Connections, $settings.InFlight, $settings.Coalesce, $settings.Adaptive, $settings.SocketReader, $settings.DedicatedWorkers, $settings.ResponseTimeoutMs, $settings.EnableSpillPressureSignals, $settings.SpillFilesThreshold, $settings.SpillActiveShardsThreshold, $settings.SpillImbalanceRatioThreshold, $settings.SpillSustainedWindowSeconds, $effectiveHybridLimit)
        $arguments = "run --project `"$projectPath`" -c Release --no-build -- --compare"
        $child = Start-Process -FilePath "dotnet" -ArgumentList $arguments -NoNewWindow -PassThru -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
        Write-Host ("  [ChildProcess] PID={0} Track={1}" -f $child.Id, $RunTrack)
        $PSNativeCommandUseErrorActionPreference = $false

        $startedAt = Get-Date
        $deadline = $startedAt.AddSeconds($ChildRunTimeoutSeconds)
        $nextHeartbeatAt = if ($ProgressHeartbeatSeconds -gt 0) { $startedAt.AddSeconds($ProgressHeartbeatSeconds) } else { [DateTime]::MaxValue }
        $timedOut = $false

        while (-not $child.HasExited) {
            [void]$child.WaitForExit(1000)
            if ($child.HasExited) {
                break
            }

            if ($StreamChildOutput) {
                $stdoutDelta = Get-NewFileLines -Path $stdoutPath -AlreadyRead $stdoutReadIndex
                foreach ($line in $stdoutDelta.Lines) {
                    Write-Host ("  [ChildStdout:{0}] {1}" -f $RunTrack, $line)
                }
                $stdoutReadIndex = $stdoutDelta.NextIndex

                $stderrDelta = Get-NewFileLines -Path $stderrPath -AlreadyRead $stderrReadIndex
                foreach ($line in $stderrDelta.Lines) {
                    Write-Warning ("  [ChildStderr:{0}] {1}" -f $RunTrack, $line)
                }
                $stderrReadIndex = $stderrDelta.NextIndex
            }

            $now = Get-Date
            if ($now -ge $nextHeartbeatAt) {
                $elapsed = $now - $startedAt
                $elapsedLabel = $elapsed.ToString('hh\:mm\:ss')
                $cpuSeconds = "n/a"
                $workingSetMb = "n/a"
                try {
                    $live = Get-Process -Id $child.Id -ErrorAction Stop
                    $cpuSeconds = ("{0:N1}" -f [double]$live.CPU)
                    $workingSetMb = ("{0:N1}" -f ($live.WorkingSet64 / 1MB))
                }
                catch {
                    # Child may have exited between checks.
                }

                Write-Host ("  [Progress] Track={0} PID={1} Elapsed={2} CPU(s)={3} WS(MB)={4} OutLines={5} ErrLines={6}" -f $RunTrack, $child.Id, $elapsedLabel, $cpuSeconds, $workingSetMb, $stdoutReadIndex, $stderrReadIndex)
                if ($ProgressHeartbeatSeconds -gt 0) {
                    $nextHeartbeatAt = $now.AddSeconds($ProgressHeartbeatSeconds)
                }
            }

            if ($now -ge $deadline) {
                $timedOut = $true
                break
            }
        }

        if ($timedOut) {
            $timeoutSnapshot = Get-DotnetProcessSnapshot
            $descendants = Get-DotnetDescendantProcessIds -Snapshot $timeoutSnapshot -RootProcessId $child.Id
            $newVapeProcesses = Get-NewVapeCacheDotnetProcessIds -BeforeSnapshot $beforeSnapshot -AfterSnapshot $timeoutSnapshot
            $cleanupCandidates = @($child.Id) + $descendants + $newVapeProcesses
            Stop-DotnetProcesses -ProcessIds $cleanupCandidates -Reason ("timeout after {0}s for track {1}" -f $ChildRunTimeoutSeconds, $RunTrack)
            throw "Benchmark child process timed out after ${ChildRunTimeoutSeconds}s for track '$RunTrack'."
        }

        # Drain any remaining buffered output once the child exits.
        if ($StreamChildOutput) {
            $stdoutDelta = Get-NewFileLines -Path $stdoutPath -AlreadyRead $stdoutReadIndex
            foreach ($line in $stdoutDelta.Lines) {
                Write-Host ("  [ChildStdout:{0}] {1}" -f $RunTrack, $line)
            }
            $stdoutReadIndex = $stdoutDelta.NextIndex

            $stderrDelta = Get-NewFileLines -Path $stderrPath -AlreadyRead $stderrReadIndex
            foreach ($line in $stderrDelta.Lines) {
                Write-Warning ("  [ChildStderr:{0}] {1}" -f $RunTrack, $line)
            }
            $stderrReadIndex = $stderrDelta.NextIndex
        }

        $completedAt = Get-Date
        $runElapsed = $completedAt - $startedAt
        $runElapsedLabel = $runElapsed.ToString('hh\:mm\:ss')
        Write-Host ("  [ChildComplete] Track={0} PID={1} ExitCode={2} Elapsed={3}" -f $RunTrack, $child.Id, $child.ExitCode, $runElapsedLabel)

        if (Test-Path $stdoutPath) {
            $runOutput += @(Get-Content -Path $stdoutPath)
        }
        if (Test-Path $stderrPath) {
            $runOutput += @(Get-Content -Path $stderrPath)
        }

        if ($null -ne $child -and $child.ExitCode -ne 0) {
            $runOutput += "dotnet run exited with code $($child.ExitCode)."
        }

        $afterSnapshot = Get-DotnetProcessSnapshot
        $newVapeProcesses = Get-NewVapeCacheDotnetProcessIds -BeforeSnapshot $beforeSnapshot -AfterSnapshot $afterSnapshot
        if ($newVapeProcesses.Count -gt 0) {
            Stop-DotnetProcesses -ProcessIds $newVapeProcesses -Reason ("post-run cleanup for track {0}" -f $RunTrack)
        }

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

        if ($hadExecutorMode) {
            $env:VAPECACHE_BENCH_VAPE_EXECUTOR_MODE = $previousExecutorMode
        }
        else {
            Remove-Item Env:VAPECACHE_BENCH_VAPE_EXECUTOR_MODE -ErrorAction SilentlyContinue
        }
        if ($hadMaxDegree) {
            $env:VAPECACHE_BENCH_MAX_DEGREE = $previousMaxDegree
        }
        else {
            Remove-Item Env:VAPECACHE_BENCH_MAX_DEGREE -ErrorAction SilentlyContinue
        }
        foreach ($name in $hybridEnvNames) {
            $value = $previousHybridEnv[$name]
            if ([string]::IsNullOrEmpty($value)) {
                Remove-Item ("Env:" + $name) -ErrorAction SilentlyContinue
            }
            else {
                [Environment]::SetEnvironmentVariable($name, $value)
            }
        }

        $PSNativeCommandUseErrorActionPreference = $previousNativeErrorPreference

        foreach ($name in $muxEnvNames) {
            $value = $previousMuxEnv[$name]
            if ([string]::IsNullOrEmpty($value)) {
                Remove-Item ("Env:" + $name) -ErrorAction SilentlyContinue
            }
            else {
                [Environment]::SetEnvironmentVariable($name, $value)
            }
        }

        Remove-Item -Path $stdoutPath -ErrorAction SilentlyContinue
        Remove-Item -Path $stderrPath -ErrorAction SilentlyContinue
    }
}

function Get-RunFailureHints([object[]]$OutputLines) {
    $hints = New-Object System.Collections.Generic.List[string]
    if ($null -eq $OutputLines -or $OutputLines.Count -eq 0) {
        return $hints
    }

    if ($OutputLines | Where-Object { "$_" -like "*LOADING Redis is loading the dataset in memory*" } | Select-Object -First 1) {
        $hints.Add("Redis reported LOADING (dataset reload in progress).")
    }

    if ($OutputLines | Where-Object { "$_" -like "*EndOfStreamException*" } | Select-Object -First 1) {
        $hints.Add("Socket read hit EndOfStreamException (transport reset/disconnect burst).")
    }

    if ($OutputLines | Where-Object { "$_" -like "*Unable to parse*" } | Select-Object -First 1) {
        $hints.Add("Harness parse failure detected in child run output.")
    }

    return $hints
}

function Invoke-IsolatedBothTrackRunWithRetry([int]$RunIndex) {
    $maxAttempts = 1 + $MaxRunRetries
    $lastOutput = @()
    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        if ($attempt -gt 1) {
            Write-Host "  Retry attempt $attempt/$maxAttempts for run $RunIndex due to previous parse failure..."
            if ($RetryDelaySeconds -gt 0) {
                Start-Sleep -Seconds $RetryDelaySeconds
            }
        }

        $applesRun = Invoke-BenchmarkRun -RunTrack "apples"
        $optimizedRun = Invoke-BenchmarkRun -RunTrack "optimized"
        $combinedOutput = @($applesRun.Output + $optimizedRun.Output)
        $lastOutput = $combinedOutput

        if ($applesRun.ParsedByTrack.Count -gt 0 -and $optimizedRun.ParsedByTrack.Count -gt 0) {
            return [pscustomobject]@{
                ApplesRun = $applesRun
                OptimizedRun = $optimizedRun
                Output = $combinedOutput
                Parsed = $true
            }
        }

        Write-Warning "Unable to parse isolated both-track throughput values on run $RunIndex (attempt $attempt/$maxAttempts)."
        $hints = Get-RunFailureHints -OutputLines $combinedOutput
        foreach ($hint in $hints) {
            Write-Warning "  Hint: $hint"
        }

        if ($attempt -eq $maxAttempts) {
            return [pscustomobject]@{
                ApplesRun = $applesRun
                OptimizedRun = $optimizedRun
                Output = $combinedOutput
                Parsed = $false
            }
        }
    }

    return [pscustomobject]@{
        ApplesRun = $null
        OptimizedRun = $null
        Output = $lastOutput
        Parsed = $false
    }
}

function Invoke-SingleTrackRunWithRetry([int]$RunIndex, [string]$RunTrack) {
    $maxAttempts = 1 + $MaxRunRetries
    $lastOutput = @()
    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        if ($attempt -gt 1) {
            Write-Host "  Retry attempt $attempt/$maxAttempts for run $RunIndex ($RunTrack) due to previous parse failure..."
            if ($RetryDelaySeconds -gt 0) {
                Start-Sleep -Seconds $RetryDelaySeconds
            }
        }

        $run = Invoke-BenchmarkRun -RunTrack $RunTrack
        $lastOutput = $run.Output
        if ($run.ParsedByTrack.Count -gt 0) {
            return [pscustomobject]@{
                Run = $run
                Output = $run.Output
                Parsed = $true
            }
        }

        Write-Warning "Unable to parse throughput output on run $RunIndex ($RunTrack) (attempt $attempt/$maxAttempts)."
        $hints = Get-RunFailureHints -OutputLines $run.Output
        foreach ($hint in $hints) {
            Write-Warning "  Hint: $hint"
        }

        if ($attempt -eq $maxAttempts) {
            return [pscustomobject]@{
                Run = $run
                Output = $run.Output
                Parsed = $false
            }
        }
    }

    return [pscustomobject]@{
        Run = $null
        Output = $lastOutput
        Parsed = $false
    }
}

$results = New-Object System.Collections.Generic.List[object]
$trackResults = @{
    ApplesToApples = New-Object System.Collections.Generic.List[object]
    OptimizedProductPath = New-Object System.Collections.Generic.List[object]
}
$isolateBoth = $Track -eq "both"
$trackSummaryByName = @{}
$trackSummaries = New-Object System.Collections.Generic.List[object]

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
        $isolatedRun = Invoke-IsolatedBothTrackRunWithRetry -RunIndex $trial
        $output = $isolatedRun.Output
        if (-not $isolatedRun.Parsed) {
            Write-Host "Unable to parse isolated both-track throughput values on run $trial."
            $output | ForEach-Object { Write-Host $_ }
            exit 2
        }

        $applesRun = $isolatedRun.ApplesRun
        $optimizedRun = $isolatedRun.OptimizedRun
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
        $singleRun = Invoke-SingleTrackRunWithRetry -RunIndex $trial -RunTrack $Track
        $output = $singleRun.Output
        if (-not $singleRun.Parsed) {
            Write-Host "Unable to parse throughput output on run $trial."
            $output | ForEach-Object { Write-Host $_ }
            exit 2
        }

        $run = $singleRun.Run
        $parsedByTrack = $run.ParsedByTrack
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
            $applesRow = New-TrialRow -Run $trial -Metrics $apples
            $trackResults["ApplesToApples"].Add($applesRow)
            Write-Host ("  ApplesToApples: Vape={0:N0} SER={1:N0} ThrRatio={2:N3} p99Ratio={3:N3} allocRatio={4:N3}" -f
                $applesRow.VapeThroughput,
                $applesRow.SerThroughput,
                $applesRow.ThroughputRatio,
                $applesRow.P99Ratio,
                $applesRow.AllocRatio)
        }
        if ($null -ne $optimized) {
            $optimizedRow = New-TrialRow -Run $trial -Metrics $optimized
            $trackResults["OptimizedProductPath"].Add($optimizedRow)
            Write-Host ("  OptimizedProductPath: Vape={0:N0} SER={1:N0} ThrRatio={2:N3} p99Ratio={3:N3} allocRatio={4:N3}" -f
                $optimizedRow.VapeThroughput,
                $optimizedRow.SerThroughput,
                $optimizedRow.ThroughputRatio,
                $optimizedRow.P99Ratio,
                $optimizedRow.AllocRatio)
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

    $row = New-TrialRow -Run $trial -Metrics $selected
    $results.Add($row)
    Write-Host ("  Vape={0:N0} SER={1:N0} ThrRatio={2:N3} p99Ratio={3:N3} allocRatio={4:N3}" -f
        $row.VapeThroughput,
        $row.SerThroughput,
        $row.ThroughputRatio,
        $row.P99Ratio,
        $row.AllocRatio)

    if ($PauseBetweenTrialsMs -gt 0 -and $trial -lt $Trials) {
        Start-Sleep -Milliseconds $PauseBetweenTrialsMs
    }
}

$vapeMedian = Get-Median -values @($results | ForEach-Object { [double]$_.VapeThroughput })
$serMedian = Get-Median -values @($results | ForEach-Object { [double]$_.SerThroughput })
$throughputRatioMedian = if ($serMedian -gt 0) { $vapeMedian / $serMedian } else { [double]::PositiveInfinity }
$vapeValues = @($results | ForEach-Object { [double]$_.VapeThroughput })
$serValues = @($results | ForEach-Object { [double]$_.SerThroughput })
$throughputRatioValues = @($results | ForEach-Object { [double]$_.ThroughputRatio })
$p50RatioValues = @($results | ForEach-Object { [double]$_.P50Ratio })
$p95RatioValues = @($results | ForEach-Object { [double]$_.P95Ratio })
$p99RatioValues = @($results | ForEach-Object { [double]$_.P99Ratio })
$allocRatioValues = @($results | ForEach-Object { [double]$_.AllocRatio })
$vapeCov = if ($vapeMedian -ne 0) { (Get-StdDev -values $vapeValues) / $vapeMedian } else { 0.0 }
$serCov = if ($serMedian -ne 0) { (Get-StdDev -values $serValues) / $serMedian } else { 0.0 }
$throughputRatioCov = if ($throughputRatioMedian -ne 0) { (Get-StdDev -values $throughputRatioValues) / $throughputRatioMedian } else { 0.0 }
$throughputRatioMin = ($throughputRatioValues | Measure-Object -Minimum).Minimum
$throughputRatioMax = ($throughputRatioValues | Measure-Object -Maximum).Maximum
$p50RatioMedian = Get-Median -values $p50RatioValues
$p95RatioMedian = Get-Median -values $p95RatioValues
$p99RatioMedian = Get-Median -values $p99RatioValues
$allocRatioMedian = Get-Median -values $allocRatioValues

Write-Host ""
Write-Host "Results:"
$results | Format-Table Run, VapeThroughput, SerThroughput, ThroughputRatio, P50Ratio, P95Ratio, P99Ratio, AllocRatio -AutoSize
Write-Host ("Median Vape throughput: {0:N0} shoppers/sec" -f $vapeMedian)
Write-Host ("Median SER throughput:  {0:N0} shoppers/sec" -f $serMedian)
Write-Host ("Median throughput ratio (Vape/SER): {0:N3}" -f $throughputRatioMedian)
Write-Host ("Median latency ratios: p50={0:N3} p95={1:N3} p99={2:N3}" -f $p50RatioMedian, $p95RatioMedian, $p99RatioMedian)
Write-Host ("Median allocation ratio (Vape/SER): {0:N3}" -f $allocRatioMedian)
Write-Host ("Stability (CoV): Vape={0:P1} SER={1:P1} ThroughputRatio={2:P1}" -f $vapeCov, $serCov, $throughputRatioCov)
Write-Host ("Throughput ratio spread: {0:N3} .. {1:N3}" -f $throughputRatioMin, $throughputRatioMax)

if ($Track -eq "both") {
    foreach ($trackName in @("ApplesToApples", "OptimizedProductPath")) {
        $summary = Get-TrackSummary -trackName $trackName -rows $trackResults[$trackName]
        if ($null -ne $summary) {
            $trackSummaries.Add($summary)
            $trackSummaryByName[$trackName] = $summary
        }
    }

    if ($trackSummaries.Count -gt 0) {
        Write-Host ""
        Write-Host "Track summary (vs SER):"
        $trackSummaries | Format-Table Track, Trials, ThroughputRatioMedian, ThroughputRatioOfMedians, P50RatioMedian, P95RatioMedian, P99RatioMedian, AllocRatioMedian, ThroughputRatioCoV, ThroughputRatioSpread -AutoSize
        foreach ($summary in $trackSummaries) {
            Write-Host ("TRACK-SUMMARY|Track={0}|Trials={1}|ThroughputRatioMedian={2:N3}|P50RatioMedian={3:N3}|P95RatioMedian={4:N3}|P99RatioMedian={5:N3}|AllocRatioMedian={6:N3}" -f
                $summary.Track,
                $summary.Trials,
                $summary.ThroughputRatioMedian,
                $summary.P50RatioMedian,
                $summary.P95RatioMedian,
                $summary.P99RatioMedian,
                $summary.AllocRatioMedian)
        }

        $geoSource = @($trackSummaries | ForEach-Object { [double]$_.ThroughputRatioMedian })
        $throughputGeoMeanAcrossTracks = Get-GeometricMean -values $geoSource
        if (-not [double]::IsNaN($throughputGeoMeanAcrossTracks)) {
            Write-Host ("Throughput geometric mean across tracks (vs SER): {0:N3}" -f $throughputGeoMeanAcrossTracks)
            Write-Host ("TRACK-GEOMEAN|ThroughputRatio={0:N3}" -f $throughputGeoMeanAcrossTracks)
        }
        Write-Host "Reporting guidance: OptimizedProductPath = hot-path comparison, ApplesToApples = parity/fallback behavior."
    }
}

if ($vapeCov -gt 0.08 -or $serCov -gt 0.08 -or $throughputRatioCov -gt 0.08) {
    Write-Warning ("Environment appears noisy (CoV > 8%). Re-run on an isolated Redis host and quiet client machine for stable comparisons.")
}

if (-not [string]::IsNullOrWhiteSpace($SummaryJsonPath)) {
    $summaryPayload = @{
        GeneratedUtc = (Get-Date).ToUniversalTime().ToString("o")
        Track = $Track
        Trials = $Trials
        ThroughputRatioMedian = [Math]::Round($throughputRatioMedian, 3)
        P50RatioMedian = [Math]::Round($p50RatioMedian, 3)
        P95RatioMedian = [Math]::Round($p95RatioMedian, 3)
        P99RatioMedian = [Math]::Round($p99RatioMedian, 3)
        AllocRatioMedian = [Math]::Round($allocRatioMedian, 3)
        TrackSummaries = @($trackSummaries | ForEach-Object { $_ })
        Gates = @{
            ThroughputMin = [double]$FailBelowRatio
            MaxP50Ratio = [double]$MaxP50Ratio
            MaxP95Ratio = [double]$MaxP95Ratio
            MaxP99Ratio = [double]$MaxP99Ratio
            MaxAllocRatio = [double]$MaxAllocRatio
            EnforceMetricGates = $EnforceMetricGates.IsPresent
        }
    }

    $summaryDirectory = Split-Path -Parent $SummaryJsonPath
    if (-not [string]::IsNullOrWhiteSpace($summaryDirectory)) {
        New-Item -ItemType Directory -Path $summaryDirectory -Force | Out-Null
    }

    $summaryPayload | ConvertTo-Json -Depth 6 | Set-Content -Path $SummaryJsonPath -Encoding utf8
    Write-Host "Summary JSON written to: $SummaryJsonPath"
}

Write-Host ("GATE-CHECK|ThroughputRatioMedian={0:N3}|MinRequired={1:N3}|EnforceMetrics={2}" -f $throughputRatioMedian, $FailBelowRatio, $EnforceMetricGates.IsPresent)
Write-Host ("GATE-CHECK|P50RatioMedian={0:N3}|P95RatioMedian={1:N3}|P99RatioMedian={2:N3}|AllocRatioMedian={3:N3}" -f $p50RatioMedian, $p95RatioMedian, $p99RatioMedian, $allocRatioMedian)

$violations = New-Object System.Collections.Generic.List[string]
if ($throughputRatioMedian -lt $FailBelowRatio) {
    $violations.Add(("Median throughput ratio {0:N3} is below threshold {1:N3}" -f $throughputRatioMedian, $FailBelowRatio))
}

if ($EnforceMetricGates) {
    if ([double]::IsNaN($p50RatioMedian) -or $p50RatioMedian -gt $MaxP50Ratio) {
        $violations.Add(("Median p50 ratio {0:N3} exceeds threshold {1:N3}" -f $p50RatioMedian, $MaxP50Ratio))
    }

    if ([double]::IsNaN($p95RatioMedian) -or $p95RatioMedian -gt $MaxP95Ratio) {
        $violations.Add(("Median p95 ratio {0:N3} exceeds threshold {1:N3}" -f $p95RatioMedian, $MaxP95Ratio))
    }

    if ([double]::IsNaN($p99RatioMedian) -or $p99RatioMedian -gt $MaxP99Ratio) {
        $violations.Add(("Median p99 ratio {0:N3} exceeds threshold {1:N3}" -f $p99RatioMedian, $MaxP99Ratio))
    }

    if ([double]::IsNaN($allocRatioMedian) -or $allocRatioMedian -gt $MaxAllocRatio) {
        $violations.Add(("Median allocation ratio {0:N3} exceeds threshold {1:N3}" -f $allocRatioMedian, $MaxAllocRatio))
    }

    if ($Track -eq "both") {
        foreach ($trackName in @("ApplesToApples", "OptimizedProductPath")) {
            if (-not $trackSummaryByName.ContainsKey($trackName)) {
                $violations.Add("Missing track summary for $trackName.")
                continue
            }

            $summary = $trackSummaryByName[$trackName]
            if ([double]$summary.P50RatioMedian -gt $MaxP50Ratio) {
                $violations.Add(("Track '{0}' p50 ratio {1:N3} exceeds threshold {2:N3}" -f $trackName, $summary.P50RatioMedian, $MaxP50Ratio))
            }
            if ([double]$summary.P95RatioMedian -gt $MaxP95Ratio) {
                $violations.Add(("Track '{0}' p95 ratio {1:N3} exceeds threshold {2:N3}" -f $trackName, $summary.P95RatioMedian, $MaxP95Ratio))
            }
            if ([double]$summary.P99RatioMedian -gt $MaxP99Ratio) {
                $violations.Add(("Track '{0}' p99 ratio {1:N3} exceeds threshold {2:N3}" -f $trackName, $summary.P99RatioMedian, $MaxP99Ratio))
            }
            if ([double]$summary.AllocRatioMedian -gt $MaxAllocRatio) {
                $violations.Add(("Track '{0}' alloc ratio {1:N3} exceeds threshold {2:N3}" -f $trackName, $summary.AllocRatioMedian, $MaxAllocRatio))
            }
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Error ("FAIL:`n{0}" -f ($violations -join "`n"))
    exit 1
}

Write-Host ("PASS: median throughput ratio {0:N3} meets threshold {1:N3}" -f $throughputRatioMedian, $FailBelowRatio)
exit 0
