[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$SkipPerfGates,
    [switch]$UsePublicSourcesOnly
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "release-common.ps1")

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
Set-Location $repoRoot

function Resolve-SolutionPath {
    foreach ($candidate in @("VapeCache.slnx")) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    throw "Could not find VapeCache.slnx."
}

$solutionPath = Resolve-SolutionPath
$nugetConfigPath = Join-Path $repoRoot "NuGet.config"
$hasNuGetConfig = Test-Path $nugetConfigPath

Write-Host "Repo: $repoRoot"
Write-Host "Solution: $solutionPath"
Write-Host "Configuration: $Configuration"

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
    dotnet build $solutionPath -c $Configuration --no-restore --nologo
}

Invoke-ReleaseStep -Name "Unit tests" -Action {
    dotnet test -c $Configuration --no-build --nologo "VapeCache.Tests/VapeCache.Tests.csproj"
}

if (-not $SkipPerfGates) {
    Invoke-ReleaseStep -Name "Perf gates tests" -Action {
        dotnet test -c $Configuration --no-build --nologo "VapeCache.PerfGates.Tests/VapeCache.PerfGates.Tests.csproj"
    }
}

Invoke-ReleaseStep -Name "Vulnerability audit" -Action {
    $auditArgs = @(
        "-Solution", $solutionPath
    )

    if ($UsePublicSourcesOnly) {
        $auditArgs += @("-PublicSource", "https://api.nuget.org/v3/index.json")
    }

    Invoke-ReleaseScript -ScriptPath (Join-Path $PSScriptRoot "audit-vulnerabilities.ps1") -ArgumentList $auditArgs
}
