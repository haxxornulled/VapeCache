param(
    [string]$Project = "VapeCache.Console\VapeCache.Console.csproj",
    [string]$Arguments = "",
    [ValidateSet("counters", "trace", "both")]
    [string]$Mode = "both",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [int]$DurationSeconds = 30,
    [string]$OutputRoot = "",
    [string]$CounterList = "System.Runtime,Microsoft.AspNetCore.Hosting"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

if ($DurationSeconds -le 0) {
    throw "DurationSeconds must be greater than zero."
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputRoot = Join-Path $repoRoot "BenchmarkDotNet.Artifacts\profiles\$timestamp"
}

New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null

$traceCli = Get-Command dotnet-trace -ErrorAction SilentlyContinue
$countersCli = Get-Command dotnet-counters -ErrorAction SilentlyContinue

if (($Mode -eq "trace" -or $Mode -eq "both") -and $null -eq $traceCli) {
    throw "dotnet-trace is required. Install with: dotnet tool install --global dotnet-trace"
}

if (($Mode -eq "counters" -or $Mode -eq "both") -and $null -eq $countersCli) {
    throw "dotnet-counters is required. Install with: dotnet tool install --global dotnet-counters"
}

function Start-ProfileTarget {
    param(
        [string]$ProjectPath,
        [string]$AppArguments,
        [string]$BuildConfiguration,
        [string]$WorkingDirectory
    )

    $runArgs = "run -c $BuildConfiguration --project `"$ProjectPath`""
    if (-not [string]::IsNullOrWhiteSpace($AppArguments)) {
        $runArgs += " -- $AppArguments"
    }

    return Start-Process -FilePath "dotnet" -ArgumentList $runArgs -WorkingDirectory $WorkingDirectory -NoNewWindow -PassThru
}

function Stop-ProfileTarget {
    param([System.Diagnostics.Process]$Process)

    if ($null -eq $Process) {
        return
    }

    if (-not $Process.HasExited) {
        Stop-Process -Id $Process.Id -Force
    }
}

function Get-CollectorDuration {
    param([int]$Seconds)
    return [TimeSpan]::FromSeconds($Seconds).ToString("hh\:mm\:ss")
}

function Collect-CountersPass {
    param(
        [string]$ProjectPath,
        [string]$AppArguments,
        [string]$BuildConfiguration,
        [string]$WorkingDirectory,
        [string]$OutputPath,
        [string]$Counters
    )

    $process = Start-ProfileTarget -ProjectPath $ProjectPath -AppArguments $AppArguments -BuildConfiguration $BuildConfiguration -WorkingDirectory $WorkingDirectory
    try {
        Start-Sleep -Seconds 3
        if ($process.HasExited) {
            throw "Profile target exited before counters collection started."
        }

        $duration = Get-CollectorDuration -Seconds $DurationSeconds
        & $countersCli.Source collect `
            --process-id $process.Id `
            --duration $duration `
            --format csv `
            --output $OutputPath `
            --counters $Counters
    }
    finally {
        Stop-ProfileTarget -Process $process
    }
}

function Collect-TracePass {
    param(
        [string]$ProjectPath,
        [string]$AppArguments,
        [string]$BuildConfiguration,
        [string]$WorkingDirectory,
        [string]$OutputPath
    )

    $process = Start-ProfileTarget -ProjectPath $ProjectPath -AppArguments $AppArguments -BuildConfiguration $BuildConfiguration -WorkingDirectory $WorkingDirectory
    try {
        Start-Sleep -Seconds 3
        if ($process.HasExited) {
            throw "Profile target exited before trace collection started."
        }

        $duration = Get-CollectorDuration -Seconds $DurationSeconds
        & $traceCli.Source collect `
            --process-id $process.Id `
            --duration $duration `
            --format nettrace `
            --output $OutputPath
    }
    finally {
        Stop-ProfileTarget -Process $process
    }
}

$projectPath = Resolve-Path $Project
$countersOutput = Join-Path $OutputRoot "counters.csv"
$traceOutput = Join-Path $OutputRoot "trace.nettrace"
$manifestPath = Join-Path $OutputRoot "profile-manifest.txt"

if ($Mode -eq "counters" -or $Mode -eq "both") {
    Write-Host "Collecting counters..."
    Collect-CountersPass `
        -ProjectPath $projectPath `
        -AppArguments $Arguments `
        -BuildConfiguration $Configuration `
        -WorkingDirectory $repoRoot `
        -OutputPath $countersOutput `
        -Counters $CounterList
}

if ($Mode -eq "trace" -or $Mode -eq "both") {
    Write-Host "Collecting trace..."
    Collect-TracePass `
        -ProjectPath $projectPath `
        -AppArguments $Arguments `
        -BuildConfiguration $Configuration `
        -WorkingDirectory $repoRoot `
        -OutputPath $traceOutput
}

@"
Project: $projectPath
Arguments: $Arguments
Mode: $Mode
Configuration: $Configuration
DurationSeconds: $DurationSeconds
CounterList: $CounterList
CountersFile: $countersOutput
TraceFile: $traceOutput
"@ | Out-File -FilePath $manifestPath -Encoding ascii

Write-Host "Profile collection complete."
Write-Host "Artifacts: $OutputRoot"
