[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$PackageOutput = "artifacts/packages",
    [string]$Source = "https://api.nuget.org/v3/index.json",
    [string]$PackageVersion = "",
    [string[]]$SkipPackageIds = @(),
    [Parameter(Mandatory = $true)]
    [string]$ApiKey
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "release-package-manifest.ps1")

$repoRoot = Get-ReleaseRepoRoot
if (-not [System.IO.Path]::IsPathRooted($PackageOutput))
{
    $PackageOutput = Join-Path $repoRoot $PackageOutput
}

$resolvedPackageVersion = Resolve-ReleasePackageVersion -PackageVersion $PackageVersion
$packages = Get-ReleasePackageVersionInfo

Assert-ReleasePackageBranding
Write-Host "Publishing release packages in dependency-safe order for version $resolvedPackageVersion"

foreach ($package in $packages)
{
    if ($SkipPackageIds -contains $package.PackageId)
    {
        Write-Host "Skipping package publish for $($package.PackageId) (configured skip list)."
        continue
    }

    $packageFile = Join-Path $PackageOutput "$($package.PackageId).$resolvedPackageVersion.nupkg"
    if (-not (Test-Path -LiteralPath $packageFile))
    {
        throw "Package artifact not found: $packageFile"
    }

    if ($PSCmdlet.ShouldProcess($packageFile, "Push to $Source"))
    {
        dotnet nuget push $packageFile --source $Source --api-key $ApiKey --skip-duplicate
        if ($LASTEXITCODE -ne 0)
        {
            throw "dotnet nuget push failed for $packageFile"
        }
    }
}
