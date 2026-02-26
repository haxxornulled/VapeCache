param(
    [string]$Job = "Short",
    [ValidateSet("fair", "realworld")]
    [string]$Mode = "fair",
    [ValidateSet("standard", "aggressive", "extreme")]
    [string]$Profile = "standard",
    [string]$ConnectionString = "",
    [string]$Interface = "",
    [int]$RedisPort = 6379,
    [string]$CaptureFilter = "",
    [switch]$SkipCapture
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$runRoot = Join-Path $repoRoot "BenchmarkDotNet.Artifacts\proof\$timestamp"
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

$benchmarkScript = Join-Path $PSScriptRoot "run-head-to-head-benchmarks.ps1"
$tshark = Get-Command tshark -ErrorAction SilentlyContinue

$pcapPath = Join-Path $runRoot "redis-capture.pcapng"
$ioStatPath = Join-Path $runRoot "wireshark-io-stat.txt"
$convPath = Join-Path $runRoot "wireshark-conversations.txt"
$manifestPath = Join-Path $runRoot "run-manifest.txt"

$effectiveFilter = if ([string]::IsNullOrWhiteSpace($CaptureFilter)) { "tcp port $RedisPort" } else { $CaptureFilter }

$captureProcess = $null

try {
    if (-not $SkipCapture) {
        if ($null -eq $tshark) {
            Write-Warning "tshark was not found. Running benchmarks without packet capture."
            $SkipCapture = $true
        }
        elseif ([string]::IsNullOrWhiteSpace($Interface)) {
            Write-Host "Available capture interfaces:"
            & $tshark.Source -D
            throw "Specify -Interface (for example 1, 2, or a named interface), or use -SkipCapture."
        }
        else {
            Write-Host "Starting packet capture..."
            Write-Host "Interface: $Interface"
            Write-Host "Filter: $effectiveFilter"
            Write-Host "PCAP: $pcapPath"

            $captureArgs = @("-i", $Interface, "-f", $effectiveFilter, "-w", $pcapPath)
            $captureProcess = Start-Process -FilePath $tshark.Source -ArgumentList $captureArgs -NoNewWindow -PassThru
            Start-Sleep -Seconds 2
        }
    }

    $benchmarkParams = @{
        Job = $Job
        Mode = $Mode
        Profile = $Profile
        ArtifactsRoot = $runRoot
    }
    if (-not [string]::IsNullOrWhiteSpace($ConnectionString)) {
        $benchmarkParams.ConnectionString = $ConnectionString
    }

    & $benchmarkScript @benchmarkParams
}
finally {
    if ($captureProcess -and -not $captureProcess.HasExited) {
        Write-Host "Stopping packet capture..."
        Stop-Process -Id $captureProcess.Id -Force
        Start-Sleep -Milliseconds 300
    }
}

if (-not $SkipCapture -and $null -ne $tshark -and (Test-Path $pcapPath)) {
    Write-Host "Generating Wireshark summaries..."
    & $tshark.Source -r $pcapPath -q -z io,stat,1 | Out-File -FilePath $ioStatPath -Encoding ascii
    & $tshark.Source -r $pcapPath -q -z conv,tcp | Out-File -FilePath $convPath -Encoding ascii
}

@"
Run Timestamp: $timestamp
Artifacts Root: $runRoot
Benchmark Job: $Job
Benchmark Mode: $Mode
Benchmark Profile: $Profile
Capture Enabled: $([bool](-not $SkipCapture))
Capture Interface: $Interface
Capture Filter: $effectiveFilter
Redis Port: $RedisPort
"@ | Out-File -FilePath $manifestPath -Encoding ascii

Write-Host ""
Write-Host "Run complete."
Write-Host "Artifacts: $runRoot"
if (Test-Path $ioStatPath) { Write-Host " - Wireshark IO stats: $ioStatPath" }
if (Test-Path $convPath) { Write-Host " - Wireshark TCP conversations: $convPath" }
