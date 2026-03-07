function Get-ReleaseRepoRoot
{
    Split-Path $PSScriptRoot -Parent
}

function Get-ReleasePackageProjects
{
    @(
        "VapeCache.Abstractions/VapeCache.Abstractions.csproj",
        "VapeCache.Features.Invalidation/VapeCache.Features.Invalidation.csproj",
        "VapeCache.Infrastructure/VapeCache.Infrastructure.csproj",
        "VapeCache.Extensions.AspNetCore/VapeCache.Extensions.AspNetCore.csproj",
        "VapeCache.Persistence/VapeCache.Persistence.csproj",
        "VapeCache.Reconciliation/VapeCache.Reconciliation.csproj",
        "VapeCache.Extensions.Aspire/VapeCache.Extensions.Aspire.csproj"
    )
}

function Get-ReleasePackageVersionInfo
{
    $infos = foreach ($project in Get-ReleasePackageProjects)
    {
        $projectPath = Join-Path (Get-ReleaseRepoRoot) $project
        if (-not (Test-Path -LiteralPath $projectPath))
        {
            throw "Release package project not found: $project"
        }

        [xml]$projectXml = Get-Content -LiteralPath $projectPath
        $packageId = $projectXml.SelectNodes("/Project/PropertyGroup/PackageId") | Select-Object -First 1
        $version = $projectXml.SelectNodes("/Project/PropertyGroup/Version") | Select-Object -First 1

        if ($null -eq $packageId -or [string]::IsNullOrWhiteSpace($packageId.InnerText))
        {
            throw "PackageId not found in $project"
        }

        if ($null -eq $version -or [string]::IsNullOrWhiteSpace($version.InnerText))
        {
            throw "Version not found in $project"
        }

        [pscustomobject]@{
            Project   = $project
            PackageId = $packageId.InnerText.Trim()
            Version   = $version.InnerText.Trim()
        }
    }

    return $infos
}

function Resolve-ReleasePackageVersion
{
    param(
        [string]$PackageVersion
    )

    $infos = Get-ReleasePackageVersionInfo
    $distinctVersions = @($infos.Version | Sort-Object -Unique)
    if ($distinctVersions.Count -ne 1)
    {
        $details = $infos |
            Sort-Object PackageId |
            ForEach-Object { "$($_.PackageId)=$($_.Version)" }
        throw "Packable package versions are inconsistent: $($details -join ', ')"
    }

    $baseVersion = $distinctVersions[0]
    if ([string]::IsNullOrWhiteSpace($PackageVersion))
    {
        return $baseVersion
    }

    $normalized = $PackageVersion.Trim()
    if ($normalized.StartsWith("v", [System.StringComparison]::OrdinalIgnoreCase))
    {
        $normalized = $normalized.Substring(1)
    }

    if ($normalized -ne $baseVersion -and -not $normalized.StartsWith("$baseVersion-", [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "Requested package version '$normalized' does not match base package version '$baseVersion'. Use '$baseVersion' or a prerelease tag like '$baseVersion-rc1'."
    }

    return $normalized
}
