param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$OutputDir = "artifacts/packages",
    [string]$PackageVersion = ""
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "release-package-manifest.ps1")
. (Join-Path $PSScriptRoot "release-common.ps1")

$repoRoot = Get-ReleaseRepoRoot
$OutputDir = Resolve-ReleaseAbsolutePath -Path $OutputDir -BasePath $repoRoot

$projects = Get-ReleasePackageProjects
$resolvedPackageVersion = Resolve-ReleasePackageVersion -PackageVersion $PackageVersion

Assert-ReleasePackageBranding

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
        $Configuration
        "--no-restore"
        "-p:Version=$resolvedPackageVersion"
        "-o"
        $OutputDir
    )

    dotnet pack @packArgs
    if ($LASTEXITCODE -ne 0)
    {
        throw "dotnet pack failed for $project"
    }
}

Get-ChildItem -LiteralPath $OutputDir -Filter *.nupkg |
    Sort-Object Name |
    ForEach-Object { Write-Host $_.Name }
