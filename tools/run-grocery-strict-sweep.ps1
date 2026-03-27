param(
    [int]$Trials = 3,
    [int]$ShopperCount = 50000,
    [int]$MaxCartSize = 40,
    [int]$PauseBetweenTrialsMs = 250,
    [ValidateSet("LowLatency", "Balanced", "FullTilt", "Custom")]
    [string[]]$Profiles = @("LowLatency", "Balanced", "FullTilt"),
    [int[]]$MuxConnectionsCandidates = @(1, 2, 4, 8),
    [int[]]$MaxDegreeCandidates = @(4, 6, 8, 10, 12),
    [ValidateSet("true", "false")]
    [string[]]$MuxAdaptiveCoalescingCandidates = @("false", "true"),
    [ValidateSet("raw", "hybrid")]
    [string]$VapeExecutorMode = "raw",
    [ValidateSet("true", "false")]
    [string]$MuxDedicatedWorkers = "true",
    [ValidateSet("true", "false")]
    [string]$MuxSocketReader = "true",
    [ValidateSet("true", "false")]
    [string]$MuxCoalesce = "true",
    [int]$MuxInFlight = 8192,
    [string]$RedisConnectionString = "",
    [string]$RedisHost = "127.0.0.1",
    [int]$RedisPort = 6379,
    [string]$RedisUsername = "",
    [string]$RedisPassword = "",
    [double]$FailBelowRatio = 1.0,
    [double]$MaxP50Ratio = 1.25,
    [double]$MaxP95Ratio = 1.30,
    [double]$MaxP99Ratio = 1.35,
    [double]$MaxAllocRatio = 1.40,
    [int]$Top = 10,
    [switch]$SkipBuild,
    [ValidateSet("Trace", "Debug", "Information", "Warning", "Error", "Critical", "None")]
    [string]$BenchLogLevel = "Debug",
    [ValidateSet("true", "false")]
    [string]$GroceryVerbose = "true",
    [ValidateSet("true", "false")]
    [string]$CleanupRunKeys = "false"
)

$ErrorActionPreference = "Stop"

if ($Trials -le 0) { throw "Trials must be greater than zero." }
if ($ShopperCount -le 0) { throw "ShopperCount must be greater than zero." }
if ($MaxCartSize -le 0) { throw "MaxCartSize must be greater than zero." }
if ($PauseBetweenTrialsMs -lt 0) { throw "PauseBetweenTrialsMs cannot be negative." }
if ($Top -le 0) { throw "Top must be greater than zero." }
if ($Profiles.Count -eq 0) { throw "Profiles cannot be empty." }
if ($MuxConnectionsCandidates.Count -eq 0) { throw "MuxConnectionsCandidates cannot be empty." }
if ($MaxDegreeCandidates.Count -eq 0) { throw "MaxDegreeCandidates cannot be empty." }
if ($MuxAdaptiveCoalescingCandidates.Count -eq 0) { throw "MuxAdaptiveCoalescingCandidates cannot be empty." }
if ($FailBelowRatio -le 0 -or $MaxP50Ratio -le 0 -or $MaxP95Ratio -le 0 -or $MaxP99Ratio -le 0 -or $MaxAllocRatio -le 0) {
    throw "Gate thresholds must be greater than zero."
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

function Get-TrackSummary([object]$summary, [string]$track) {
    if ($null -eq $summary -or $null -eq $summary.TrackSummaries) {
        return $null
    }

    return @($summary.TrackSummaries) | Where-Object { $_.Track -eq $track } | Select-Object -First 1
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$headToHeadScript = Join-Path $repoRoot "tools/run-grocery-head-to-head.ps1"
$projectPath = Join-Path $repoRoot "VapeCache.Console/VapeCache.Console.csproj"
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$artifactRoot = Join-Path $repoRoot "BenchmarkDotNet.Artifacts/grocery-strict-sweep/$timestamp"
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null

Write-Host "Strict grocery sweep"
Write-Host "Artifacts: $artifactRoot"
Write-Host "Profiles: $($Profiles -join ', ')"
Write-Host "MuxConnectionsCandidates: $($MuxConnectionsCandidates -join ', ')"
Write-Host "MaxDegreeCandidates: $($MaxDegreeCandidates -join ', ')"
Write-Host "MuxAdaptiveCoalescingCandidates: $($MuxAdaptiveCoalescingCandidates -join ', ')"
Write-Host "Gates: Throughput>=$FailBelowRatio p50<=$MaxP50Ratio p95<=$MaxP95Ratio p99<=$MaxP99Ratio alloc<=$MaxAllocRatio"

if (-not $SkipBuild) {
    Write-Host ""
    Write-Host "Building Release binaries..."
    dotnet build "$projectPath" -c Release --nologo | Out-Host
}

$results = New-Object System.Collections.Generic.List[object]
$candidateIndex = 0
$totalCandidates = $Profiles.Count * $MuxConnectionsCandidates.Count * $MaxDegreeCandidates.Count * $MuxAdaptiveCoalescingCandidates.Count

foreach ($profile in $Profiles) {
    foreach ($muxConnections in $MuxConnectionsCandidates) {
        foreach ($maxDegree in $MaxDegreeCandidates) {
            foreach ($adaptive in $MuxAdaptiveCoalescingCandidates) {
                $candidateIndex++
                $candidateName = "p-$($profile)-c$($muxConnections)-d$($maxDegree)-a$($adaptive)"
                $safeCandidateName = $candidateName.ToLowerInvariant().Replace(" ", "-")
                $summaryPath = Join-Path $artifactRoot "$safeCandidateName.summary.json"
                $logPath = Join-Path $artifactRoot "$safeCandidateName.log"

                Write-Host ""
                Write-Host ("[{0}/{1}] {2}" -f $candidateIndex, $totalCandidates, $candidateName)

                $args = @{
                    Trials = $Trials
                    ShopperCount = $ShopperCount
                    MaxCartSize = $MaxCartSize
                    MaxDegree = $maxDegree
                    Track = "both"
                    VapeExecutorMode = $VapeExecutorMode
                    MuxProfile = $profile
                    MuxConnections = $muxConnections
                    MuxInFlight = $MuxInFlight
                    MuxCoalesce = $MuxCoalesce
                    MuxAdaptiveCoalescing = $adaptive
                    MuxSocketReader = $MuxSocketReader
                    MuxDedicatedWorkers = $MuxDedicatedWorkers
                    DisableTrackDefaults = $true
                    EnforceMetricGates = $true
                    FailBelowRatio = $FailBelowRatio
                    MaxP50Ratio = $MaxP50Ratio
                    MaxP95Ratio = $MaxP95Ratio
                    MaxP99Ratio = $MaxP99Ratio
                    MaxAllocRatio = $MaxAllocRatio
                    PauseBetweenTrialsMs = $PauseBetweenTrialsMs
                    BenchLogLevel = $BenchLogLevel
                    GroceryVerbose = $GroceryVerbose
                    CleanupRunKeys = $CleanupRunKeys
                    SkipBuild = $true
                    SummaryJsonPath = $summaryPath
                }

                if (-not [string]::IsNullOrWhiteSpace($RedisConnectionString)) {
                    $args["RedisConnectionString"] = $RedisConnectionString
                }
                else {
                    $args["RedisHost"] = $RedisHost
                    $args["RedisPort"] = $RedisPort
                    $args["RedisUsername"] = $RedisUsername
                    $args["RedisPassword"] = $RedisPassword
                }

                $exitCode = 0
                $errorText = ""
                try {
                    & $headToHeadScript @args 2>&1 | Tee-Object -FilePath $logPath | Out-Host
                    $exitCode = if ($null -ne $LASTEXITCODE) { [int]$LASTEXITCODE } else { 0 }
                }
                catch {
                    $exitCode = 1
                    $errorText = $_.Exception.Message
                    $_ | Out-String | Set-Content -Path $logPath -Encoding utf8
                    Write-Warning ("Candidate failed with exception: {0}" -f $errorText)
                }

                $summary = $null
                if (Test-Path $summaryPath) {
                    try {
                        $summary = Get-Content -Raw -Path $summaryPath | ConvertFrom-Json
                    }
                    catch {
                        Write-Warning ("Unable to parse summary JSON for candidate {0}" -f $candidateName)
                    }
                }

                $apples = Get-TrackSummary -summary $summary -track "ApplesToApples"
                $optimized = Get-TrackSummary -summary $summary -track "OptimizedProductPath"
                $throughputGeoMean = [double]::NaN
                if ($null -ne $apples -and $null -ne $optimized) {
                    $throughputGeoMean = Get-GeometricMean -values @([double]$apples.ThroughputRatioMedian, [double]$optimized.ThroughputRatioMedian)
                }

                $results.Add([pscustomobject]@{
                    Candidate = $candidateName
                    Profile = $profile
                    MuxConnections = $muxConnections
                    MaxDegree = $maxDegree
                    Adaptive = $adaptive
                    ExitCode = $exitCode
                    Passed = ($exitCode -eq 0)
                    ThroughputGeoMean = [Math]::Round($throughputGeoMean, 4)
                    ApplesThroughputRatioMedian = if ($null -ne $apples) { [Math]::Round([double]$apples.ThroughputRatioMedian, 4) } else { [double]::NaN }
                    OptimizedThroughputRatioMedian = if ($null -ne $optimized) { [Math]::Round([double]$optimized.ThroughputRatioMedian, 4) } else { [double]::NaN }
                    ApplesP99RatioMedian = if ($null -ne $apples) { [Math]::Round([double]$apples.P99RatioMedian, 4) } else { [double]::NaN }
                    OptimizedP99RatioMedian = if ($null -ne $optimized) { [Math]::Round([double]$optimized.P99RatioMedian, 4) } else { [double]::NaN }
                    ApplesAllocRatioMedian = if ($null -ne $apples) { [Math]::Round([double]$apples.AllocRatioMedian, 4) } else { [double]::NaN }
                    OptimizedAllocRatioMedian = if ($null -ne $optimized) { [Math]::Round([double]$optimized.AllocRatioMedian, 4) } else { [double]::NaN }
                    SummaryPath = $summaryPath
                    LogPath = $logPath
                    Error = $errorText
                }) | Out-Null
            }
        }
    }
}

Write-Host ""
Write-Host "Sweep results:"
$results |
    Sort-Object -Property @{ Expression = "Passed"; Descending = $true }, @{ Expression = "ThroughputGeoMean"; Descending = $true } |
    Format-Table Candidate, Passed, ThroughputGeoMean, ApplesThroughputRatioMedian, OptimizedThroughputRatioMedian, ApplesP99RatioMedian, OptimizedP99RatioMedian, ApplesAllocRatioMedian, OptimizedAllocRatioMedian -AutoSize

$topPassed = @(
    $results |
        Where-Object { $_.Passed -and -not [double]::IsNaN([double]$_.ThroughputGeoMean) } |
        Sort-Object -Property ThroughputGeoMean -Descending |
        Select-Object -First $Top
)

$summaryJsonPath = Join-Path $artifactRoot "strict-sweep-summary.json"
$summaryMdPath = Join-Path $artifactRoot "strict-sweep-summary.md"

$summaryPayload = [pscustomobject]@{
    GeneratedUtc = (Get-Date).ToUniversalTime().ToString("o")
    Trials = $Trials
    ShopperCount = $ShopperCount
    MaxCartSize = $MaxCartSize
    CandidateCount = $results.Count
    PassedCount = (@($results | Where-Object { $_.Passed })).Count
    TopPassed = $topPassed
    Results = $results
}

$summaryPayload | ConvertTo-Json -Depth 6 | Set-Content -Path $summaryJsonPath -Encoding utf8

$mdLines = New-Object System.Collections.Generic.List[string]
$mdLines.Add("# Grocery Strict Sweep Summary")
$mdLines.Add("")
$mdLines.Add(("Generated: {0} UTC" -f (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss")))
$mdLines.Add("")
$mdLines.Add(("Trials={0}, Shoppers={1}, MaxCartSize={2}" -f $Trials, $ShopperCount, $MaxCartSize))
$mdLines.Add(("Gates: Throughput>={0}, p50<={1}, p95<={2}, p99<={3}, alloc<={4}" -f $FailBelowRatio, $MaxP50Ratio, $MaxP95Ratio, $MaxP99Ratio, $MaxAllocRatio))
$mdLines.Add("")
$mdLines.Add("| Candidate | Passed | Throughput GeoMean | Apples Thr Ratio | Optimized Thr Ratio | Apples p99 Ratio | Optimized p99 Ratio | Apples Alloc Ratio | Optimized Alloc Ratio |")
$mdLines.Add("|---|---|---:|---:|---:|---:|---:|---:|---:|")
foreach ($row in $topPassed) {
    $mdLines.Add(
        ("| {0} | {1} | {2:N4} | {3:N4} | {4:N4} | {5:N4} | {6:N4} | {7:N4} | {8:N4} |" -f
            $row.Candidate,
            $(if ($row.Passed) { "yes" } else { "no" }),
            [double]$row.ThroughputGeoMean,
            [double]$row.ApplesThroughputRatioMedian,
            [double]$row.OptimizedThroughputRatioMedian,
            [double]$row.ApplesP99RatioMedian,
            [double]$row.OptimizedP99RatioMedian,
            [double]$row.ApplesAllocRatioMedian,
            [double]$row.OptimizedAllocRatioMedian))
}

$mdLines | Set-Content -Path $summaryMdPath -Encoding utf8

Write-Host ""
Write-Host "Summary JSON: $summaryJsonPath"
Write-Host "Summary Markdown: $summaryMdPath"

if ($topPassed.Count -eq 0) {
    Write-Error "No strict-profile candidates passed the gates."
    exit 1
}

Write-Host "Strict sweep completed."
exit 0
