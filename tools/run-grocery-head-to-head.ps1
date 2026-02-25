param(
    [int]$Trials = 5,
    [int]$ShopperCount = 50000,
    [int]$MaxCartSize = 40,
    [ValidateSet("optimized", "apples")]
    [string]$Track = "optimized",
    [ValidateSet("FullTilt", "Balanced", "LowLatency", "Custom")]
    [string]$MuxProfile = "LowLatency",
    [int]$MuxConnections = 4,
    [int]$MuxInFlight = 8192,
    [ValidateSet("true", "false")]
    [string]$MuxCoalesce = "true",
    [int]$MuxResponseTimeoutMs = 0,
    [double]$FailBelowRatio = 1.0
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

if ($MuxConnections -le 0) {
    throw "MuxConnections must be greater than zero."
}

if ($MuxInFlight -le 0) {
    throw "MuxInFlight must be greater than zero."
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repoRoot "VapeCache.Console\VapeCache.Console.csproj"

$env:VAPECACHE_MAX_CART_SIZE = "$MaxCartSize"
$env:VAPECACHE_BENCH_TRACK = $Track
$env:VAPECACHE_BENCH_MUX_PROFILE = $MuxProfile
$env:VAPECACHE_BENCH_MUX_CONNECTIONS = "$MuxConnections"
$env:VAPECACHE_BENCH_MUX_INFLIGHT = "$MuxInFlight"
$env:VAPECACHE_BENCH_MUX_COALESCE = $MuxCoalesce.ToLowerInvariant()
$env:VAPECACHE_BENCH_MUX_RESPONSE_TIMEOUT_MS = "$MuxResponseTimeoutMs"

Write-Host "Grocery head-to-head benchmark"
Write-Host "Trials: $Trials"
Write-Host "Shoppers: $ShopperCount"
Write-Host "Max cart size: $MaxCartSize"
Write-Host "Track: $Track"
Write-Host "Mux: Profile=$MuxProfile Connections=$MuxConnections InFlight=$MuxInFlight Coalesce=$($env:VAPECACHE_BENCH_MUX_COALESCE) TimeoutMs=$MuxResponseTimeoutMs"
Write-Host ""

function Get-Median([double[]]$values) {
    if ($values.Count -eq 0) {
        return 0.0
    }

    $sorted = $values | Sort-Object
    $count = $sorted.Count
    if (($count % 2) -eq 1) {
        return [double]$sorted[[int]($count / 2)]
    }

    return ([double]$sorted[$count / 2 - 1] + [double]$sorted[$count / 2]) / 2.0
}

function Get-InputLines([int]$shopperCount) {
    # Menu now accepts a direct shopper count (no preset selector).
    return @("$shopperCount")
}

$results = New-Object System.Collections.Generic.List[object]

for ($trial = 1; $trial -le $Trials; $trial++) {
    Write-Host "Run $trial/$Trials..."
    $inputLines = Get-InputLines -shopperCount $ShopperCount
    $inputFile = [System.IO.Path]::GetTempFileName()
    try {
        Set-Content -Path $inputFile -Value $inputLines -Encoding ascii
        $cmd = "dotnet run --project ""$projectPath"" -c Release --no-build -- --compare < ""$inputFile"""
        $output = cmd /c $cmd
    }
    finally {
        Remove-Item -Path $inputFile -Force -ErrorAction SilentlyContinue
    }

    $throughputLines = $output | Select-String -Pattern "Throughput:\s+([0-9,]+)\s+shoppers/sec"
    if ($throughputLines.Count -lt 2) {
        Write-Host "Unable to parse throughput output on run $trial."
        $output | ForEach-Object { Write-Host $_ }
        exit 2
    }

    $vape = [double](($throughputLines[0].Matches[0].Groups[1].Value) -replace ",", "")
    $ser = [double](($throughputLines[1].Matches[0].Groups[1].Value) -replace ",", "")
    $ratio = if ($ser -gt 0) { $vape / $ser } else { [double]::PositiveInfinity }

    $results.Add([pscustomobject]@{
        Run = $trial
        VapeThroughput = $vape
        SerThroughput = $ser
        Ratio = [Math]::Round($ratio, 3)
    })

    Write-Host ("  Vape={0:N0} SER={1:N0} Ratio={2:N3}" -f $vape, $ser, $ratio)
}

$vapeMedian = Get-Median -values @($results | ForEach-Object { [double]$_.VapeThroughput })
$serMedian = Get-Median -values @($results | ForEach-Object { [double]$_.SerThroughput })
$ratioMedian = if ($serMedian -gt 0) { $vapeMedian / $serMedian } else { [double]::PositiveInfinity }

Write-Host ""
Write-Host "Results:"
$results | Format-Table Run, VapeThroughput, SerThroughput, Ratio -AutoSize
Write-Host ("Median Vape throughput: {0:N0} shoppers/sec" -f $vapeMedian)
Write-Host ("Median SER throughput:  {0:N0} shoppers/sec" -f $serMedian)
Write-Host ("Median Ratio (Vape/SER): {0:N3}" -f $ratioMedian)

if ($ratioMedian -lt $FailBelowRatio) {
    Write-Error ("FAIL: median Vape/SER ratio {0:N3} is below threshold {1:N3}" -f $ratioMedian, $FailBelowRatio)
    exit 1
}

Write-Host ("PASS: median Vape/SER ratio {0:N3} meets threshold {1:N3}" -f $ratioMedian, $FailBelowRatio)
exit 0
