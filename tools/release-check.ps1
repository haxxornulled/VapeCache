param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipBaseline,
    [switch]$SkipPerfGates,
    [switch]$SkipPack,
    [switch]$IncludeVulnerabilityAudit
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

function Invoke-Step {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [scriptblock]$Action
    )

    Write-Host ""
    Write-Host "==> $Name"
    Remove-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
    & $Action
    if ($null -ne $global:LASTEXITCODE -and $global:LASTEXITCODE -ne 0) {
        throw "Step failed: $Name (exit code: $global:LASTEXITCODE)"
    }
}

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
$releaseManifestPath = Join-Path $PSScriptRoot "release-package-manifest.ps1"

Write-Host "Repo: $repoRoot"
Write-Host "Solution: $solutionPath"
Write-Host "Configuration: $Configuration"

if (Test-Path $releaseManifestPath) {
    . $releaseManifestPath
    Invoke-Step -Name "Release package branding metadata" -Action {
        Assert-ReleasePackageBranding
    }
}

Invoke-Step -Name "Restore" -Action {
    if ($hasNuGetConfig) {
        dotnet restore $solutionPath --configfile $nugetConfigPath
    }
    else {
        dotnet restore $solutionPath
    }
}

Invoke-Step -Name "Build" -Action {
    dotnet build -c $Configuration --no-restore $solutionPath
}

if (-not $SkipBaseline -and (Test-Path (Join-Path $PSScriptRoot "verify-runtime-warning-baseline.ps1"))) {
    Invoke-Step -Name "Runtime analyzer baseline" -Action {
        pwsh -File (Join-Path $PSScriptRoot "verify-runtime-warning-baseline.ps1") -Configuration $Configuration
    }
}

if ($IncludeVulnerabilityAudit -and (Test-Path (Join-Path $PSScriptRoot "audit-vulnerabilities.ps1"))) {
    Invoke-Step -Name "Vulnerability audit (public feeds)" -Action {
        pwsh -File (Join-Path $PSScriptRoot "audit-vulnerabilities.ps1") -Solution $solutionPath
    }
}

Invoke-Step -Name "Unit tests" -Action {
    dotnet test -c $Configuration --no-build "VapeCache.Tests/VapeCache.Tests.csproj"
}

if (-not $SkipPerfGates) {
    Invoke-Step -Name "Perf gates tests" -Action {
        dotnet test -c $Configuration --no-build "VapeCache.PerfGates.Tests/VapeCache.PerfGates.Tests.csproj"
    }
}

if (-not $SkipPack) {
    $packScript = Join-Path $PSScriptRoot "pack-release-packages.ps1"
    $smokeScript = Join-Path $PSScriptRoot "package-smoke.ps1"
    $packOutput = "artifacts/release-check-packages"

    if (Test-Path $packScript) {
        Invoke-Step -Name "Pack release packages" -Action {
            pwsh -File $packScript -OutputDir $packOutput
        }
    }

    if (Test-Path $smokeScript) {
        Invoke-Step -Name "Package smoke test (VapeCache.Runtime)" -Action {
            pwsh -File $smokeScript -PackageOutput $packOutput -PackageId "VapeCache.Runtime"
        }
    }
}

$elapsed = (Get-Date) - $start
Write-Host ""
Write-Host "Release readiness checks passed in $([int]$elapsed.TotalMinutes)m $([int]$elapsed.Seconds)s."
