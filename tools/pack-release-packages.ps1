param(
    [string]$OutputDir = "artifacts/packages",
    [string]$PackageVersion = ""
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "release-package-manifest.ps1")

$repoRoot = Get-ReleaseRepoRoot
if (-not [System.IO.Path]::IsPathRooted($OutputDir))
{
    $OutputDir = Join-Path $repoRoot $OutputDir
}

$projects = Get-ReleasePackageProjects
$coreProject = "VapeCache.Core/VapeCache.Core.csproj"
$projects = @($coreProject) + $projects
$resolvedPackageVersion = Resolve-ReleasePackageVersion -PackageVersion $PackageVersion

if (Test-Path -LiteralPath $OutputDir)
{
    Remove-Item -LiteralPath $OutputDir -Recurse -Force
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

Write-Host "Packing release packages with version $resolvedPackageVersion"

foreach ($project in $projects)
{
    $projectPath = Join-Path $repoRoot $project
    Write-Host "Packing $project"

    $packArgs = @(
        $projectPath
        "-c"
        "Release"
        "--no-restore"
        "-p:Version=$resolvedPackageVersion"
        "-o"
        $OutputDir
    )

    if ($project -eq $coreProject)
    {
        # VapeCache.Abstractions carries a package dependency on VapeCache.Core.
        # Emit an explicit core package in release artifacts so smoke restore resolves locally.
        $packArgs += "-p:IsPackable=true"
    }

    dotnet pack @packArgs
    if ($LASTEXITCODE -ne 0)
    {
        throw "dotnet pack failed for $project"
    }
}

Get-ChildItem -LiteralPath $OutputDir -Filter *.nupkg |
    Sort-Object Name |
    ForEach-Object { Write-Host $_.Name }
