param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$OutputDir = "artifacts/packages",
    [string]$PackageVersion = "",
    [string[]]$RestoreSource = @(),
    [ValidateRange(1, 128)]
    [int]$MaxCpuCount = 1,
    [switch]$IgnoreFailedRestoreSources,
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "release-package-manifest.ps1")
. (Join-Path $PSScriptRoot "release-common.ps1")

$repoRoot = Get-ReleaseRepoRoot
$OutputDir = Resolve-ReleaseAbsolutePath -Path $OutputDir -BasePath $repoRoot
$nugetConfigPath = Join-Path $repoRoot "NuGet.config"
$hasNuGetConfig = Test-Path -LiteralPath $nugetConfigPath

$projects = Get-ReleasePackageProjects
$resolvedPackageVersion = Resolve-ReleasePackageVersion -PackageVersion $PackageVersion

Assert-ReleasePackageBranding

if (Test-Path -LiteralPath $OutputDir)
{
    Remove-Item -LiteralPath $OutputDir -Recurse -Force
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

Write-Host "Packing release packages with version $resolvedPackageVersion"
Write-Host "Using up to $MaxCpuCount MSBuild node(s) for package packing"

foreach ($project in $projects)
{
    $projectPath = Join-Path $repoRoot $project

    if (-not $NoRestore)
    {
        Write-Host "Restoring $project"
        $restoreArgs = @($projectPath)

        if ($RestoreSource.Count -gt 0)
        {
            foreach ($source in $RestoreSource)
            {
                $restoreArgs += @("--source", $source)
            }
        }
        elseif ($hasNuGetConfig)
        {
            $restoreArgs += @("--configfile", $nugetConfigPath)
        }

        if ($IgnoreFailedRestoreSources)
        {
            $restoreArgs += "-p:RestoreIgnoreFailedSources=true"
        }

        $restoreArgs += "-m:$MaxCpuCount"

        dotnet restore @restoreArgs
        if ($LASTEXITCODE -ne 0)
        {
            throw "dotnet restore failed for $project"
        }
    }

    Write-Host "Packing $project"

    $packArgs = @(
        $projectPath
        "-c"
        $Configuration
        "-p:Version=$resolvedPackageVersion"
        "-p:ContinuousIntegrationBuild=true"
        "-o"
        $OutputDir
    )

    $packArgs += "-m:$MaxCpuCount"
    $packArgs += "--no-restore"

    dotnet pack @packArgs
    if ($LASTEXITCODE -ne 0)
    {
        throw "dotnet pack failed for $project"
    }
}

Get-ChildItem -LiteralPath $OutputDir -Filter *.nupkg |
    Sort-Object Name |
    ForEach-Object { Write-Host $_.Name }
