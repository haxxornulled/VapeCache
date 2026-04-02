[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$PackageOutput = "artifacts/packages",
    [string]$Source = "https://api.nuget.org/v3/index.json",
    [string]$PackageVersion = "",
    [string[]]$SkipPackageIds = @(),
    [string]$ApiKey = $env:NUGET_API_KEY
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "release-package-manifest.ps1")
. (Join-Path $PSScriptRoot "release-common.ps1")

$repoRoot = Get-ReleaseRepoRoot
$PackageOutput = Resolve-ReleaseAbsolutePath -Path $PackageOutput -BasePath $repoRoot

$resolvedPackageVersion = Resolve-ReleasePackageVersion -PackageVersion $PackageVersion
$packages = Get-ReleasePackageVersionInfo

Assert-ReleasePackageBranding
Assert-ReleasePackageDocumentationMetadata

if ([string]::IsNullOrWhiteSpace($ApiKey))
{
    throw "NuGet API key required. Pass -ApiKey or set NUGET_API_KEY."
}

Write-Host "Publishing release packages in dependency-safe order for version $resolvedPackageVersion"

$artifacts = Get-ReleasePackageArtifacts -Packages $packages -PackageOutput $PackageOutput -PackageVersion $resolvedPackageVersion
foreach ($artifact in $artifacts)
{
    if ($SkipPackageIds -contains $artifact.PackageId)
    {
        Write-Host "Skipping package publish for $($artifact.PackageId) (configured skip list)."
        continue
    }

    if ($PSCmdlet.ShouldProcess($artifact.PackageFile, "Push to $Source"))
    {
        dotnet nuget push $artifact.PackageFile --source $Source --api-key $ApiKey --skip-duplicate
        if ($LASTEXITCODE -ne 0)
        {
            throw "dotnet nuget push failed for $($artifact.PackageFile)"
        }
    }
}
