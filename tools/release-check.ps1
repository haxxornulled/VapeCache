param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipBaseline,
    [switch]$SkipPerfGates,
    [switch]$SkipPack,
    [switch]$IncludeVulnerabilityAudit,
    [switch]$UsePublicSourcesOnly
)

$ErrorActionPreference = "Stop"

$releaseManifestPath = Join-Path $PSScriptRoot "release-package-manifest.ps1"
$releaseCommonPath = Join-Path $PSScriptRoot "release-common.ps1"

. $releaseManifestPath
. $releaseCommonPath

$repoRoot = Get-ReleaseRepoRoot
Set-Location $repoRoot

function Resolve-SolutionPath {
    foreach ($candidate in @("VapeCache.slnx")) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    throw "Could not find VapeCache.slnx."
}

$start = Get-Date
$solutionPath = Resolve-SolutionPath
$nugetConfigPath = Join-Path $repoRoot "NuGet.config"
$hasNuGetConfig = Test-Path $nugetConfigPath

Write-Host "Repo: $repoRoot"
Write-Host "Solution: $solutionPath"
Write-Host "Configuration: $Configuration"

Invoke-ReleaseStep -Name "Release package branding metadata" -Action {
    Assert-ReleasePackageBranding
}

Invoke-ReleaseStep -Name "Restore" -Action {
    if ($UsePublicSourcesOnly) {
        dotnet restore $solutionPath --source "https://api.nuget.org/v3/index.json"
    }
    elseif ($hasNuGetConfig) {
        dotnet restore $solutionPath --configfile $nugetConfigPath
    }
    else {
        dotnet restore $solutionPath
    }
}

Invoke-ReleaseStep -Name "Build" -Action {
    dotnet build -c $Configuration --no-restore $solutionPath
}

if (-not $SkipBaseline -and (Test-Path (Join-Path $PSScriptRoot "verify-runtime-warning-baseline.ps1"))) {
    Invoke-ReleaseStep -Name "Runtime analyzer baseline" -Action {
        Invoke-ReleaseScript -ScriptPath (Join-Path $PSScriptRoot "verify-runtime-warning-baseline.ps1") -ArgumentList @(
            "-Configuration", $Configuration
        )
    }
}

if ($IncludeVulnerabilityAudit -and (Test-Path (Join-Path $PSScriptRoot "audit-vulnerabilities.ps1"))) {
    Invoke-ReleaseStep -Name "Vulnerability audit (public feeds)" -Action {
        Invoke-ReleaseScript -ScriptPath (Join-Path $PSScriptRoot "audit-vulnerabilities.ps1") -ArgumentList @(
            "-Solution", $solutionPath
        )
    }
}

Invoke-ReleaseStep -Name "Unit tests" -Action {
    dotnet test -c $Configuration --no-build "VapeCache.Tests/VapeCache.Tests.csproj"
}

if (-not $SkipPerfGates) {
    Invoke-ReleaseStep -Name "Perf gates tests" -Action {
        dotnet test -c $Configuration --no-build "VapeCache.PerfGates.Tests/VapeCache.PerfGates.Tests.csproj"
    }
}

if (-not $SkipPack) {
    $packScript = Join-Path $PSScriptRoot "pack-release-packages.ps1"
    $smokeScript = Join-Path $PSScriptRoot "package-smoke.ps1"
    $packOutput = "artifacts/release-check-packages"

    if (Test-Path $packScript) {
        Invoke-ReleaseStep -Name "Pack release packages" -Action {
            Invoke-ReleaseScript -ScriptPath $packScript -ArgumentList @(
                "-Configuration", $Configuration,
                "-OutputDir", $packOutput
            )
        }
    }

    if (Test-Path $smokeScript) {
        foreach ($packageId in Get-ReleaseSmokePackageIds) {
            Invoke-ReleaseStep -Name "Package smoke test ($packageId)" -Action {
                Invoke-ReleaseScript -ScriptPath $smokeScript -ArgumentList @(
                    "-Configuration", $Configuration,
                    "-PackageOutput", $packOutput,
                    "-PackageId", $packageId
                )
            }
        }
    }
}

$elapsed = (Get-Date) - $start
Write-Host ""
Write-Host "Release readiness checks passed in $([int]$elapsed.TotalMinutes)m $([int]$elapsed.Seconds)s."
