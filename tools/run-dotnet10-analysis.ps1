param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$TreatWarningsAsErrors,
    [switch]$RunTests = $true,
    [switch]$VerifyFormatting,
    [switch]$StopBenchmarkRunners
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

function Resolve-SolutionPath {
    foreach ($candidate in @("VapeCache.slnx")) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    throw "Could not find 'VapeCache.slnx' in $repoRoot."
}

function Invoke-DotNetOrThrow {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed (dotnet exit code: $LASTEXITCODE)."
    }
}

function Get-RepoBenchmarkRunners {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot
    )

    $benchRoot = Join-Path $RepoRoot "VapeCache.Benchmarks"
    Get-Process -Name "VapeCache.Benchmarks.Runner" -ErrorAction SilentlyContinue |
        Where-Object {
            try {
                $_.Path -and $_.Path.StartsWith($benchRoot, [StringComparison]::OrdinalIgnoreCase)
            }
            catch {
                $false
            }
        }
}

$activeBenchmarks = @(Get-RepoBenchmarkRunners -RepoRoot $repoRoot)
if ($activeBenchmarks.Count -gt 0) {
    $ids = ($activeBenchmarks | ForEach-Object { $_.Id }) -join ", "
    if ($StopBenchmarkRunners) {
        Write-Host "Stopping active VapeCache.Benchmarks.Runner processes: $ids"
        $activeBenchmarks | ForEach-Object {
            Stop-Process -Id $_.Id -Force -ErrorAction Stop
        }
    }
    else {
        throw "Detected running VapeCache.Benchmarks.Runner process(es) [$ids] from this repo. Stop them or rerun with -StopBenchmarkRunners."
    }
}

Write-Host "Running .NET 10 analyzer build for VapeCache..."

$solutionPath = Resolve-SolutionPath

$buildArgs = @(
    "build",
    $solutionPath,
    "-t:Rebuild",
    "-c", $Configuration,
    "/p:EnableNETAnalyzers=true",
    "/p:AnalysisLevel=latest-recommended",
    "/p:EnforceCodeStyleInBuild=true"
)

if ($TreatWarningsAsErrors) {
    $buildArgs += "/warnaserror"
}

Invoke-DotNetOrThrow -Arguments $buildArgs -Description "Analyzer build"

if ($VerifyFormatting) {
    Write-Host "Verifying analyzer/code-style formatting..."
    Invoke-DotNetOrThrow -Arguments @("format", "analyzers", $solutionPath, "--verify-no-changes", "--severity", "warn") -Description "Analyzer format verification"
}

if ($RunTests) {
    Write-Host "Running test suite..."
    Invoke-DotNetOrThrow -Arguments @("test", "VapeCache.Tests\VapeCache.Tests.csproj", "-c", $Configuration, "--no-build") -Description "Test suite"
    Invoke-DotNetOrThrow -Arguments @("test", "VapeCache.PerfGates.Tests\VapeCache.PerfGates.Tests.csproj", "-c", $Configuration, "--no-build") -Description "Perf gate suite"
}

Write-Host "Analyzer pipeline completed successfully."
