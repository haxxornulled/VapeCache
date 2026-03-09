param(
    [string]$PackageOutput = "artifacts/packages",
    [string[]]$AdditionalPackageSources = @(),
    [string]$GitHubPackagesSource = "https://nuget.pkg.github.com/haxxornulled/index.json",
    [string]$GitHubPackagesUser = "",
    [string]$GitHubPackagesToken = ""
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "release-package-manifest.ps1")

$packageIds = [System.Collections.Generic.List[string]]::new()
$packageIds.Add("VapeCache.Core")

foreach ($info in Get-ReleasePackageVersionInfo)
{
    if ($packageIds -notcontains $info.PackageId)
    {
        $packageIds.Add($info.PackageId)
    }
}

$failures = [System.Collections.Generic.List[string]]::new()

foreach ($packageId in $packageIds)
{
    Write-Host "Smoke testing $packageId"

    try
    {
        & (Join-Path $PSScriptRoot "package-smoke.ps1") `
            -PackageOutput $PackageOutput `
            -PackageId $packageId `
            -AdditionalPackageSources $AdditionalPackageSources `
            -GitHubPackagesSource $GitHubPackagesSource `
            -GitHubPackagesUser $GitHubPackagesUser `
            -GitHubPackagesToken $GitHubPackagesToken

        if ($LASTEXITCODE -ne 0)
        {
            throw "package-smoke returned exit code $LASTEXITCODE."
        }
    }
    catch
    {
        Write-Warning "Smoke test failed for $packageId. $($_.Exception.Message)"
        $failures.Add($packageId)
    }
}

if ($failures.Count -gt 0)
{
    throw "Release package smoke failed for: $($failures -join ', ')"
}
