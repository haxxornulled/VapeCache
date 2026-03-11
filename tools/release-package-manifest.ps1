function Get-ReleaseRepoRoot
{
    Split-Path $PSScriptRoot -Parent
}

function Get-ReleasePackageProjects
{
    @(
        "VapeCache.Core/VapeCache.Core.csproj",
        "VapeCache.Abstractions/VapeCache.Abstractions.csproj",
        "VapeCache.Features.Invalidation/VapeCache.Features.Invalidation.csproj",
        "VapeCache.Infrastructure/VapeCache.Infrastructure.csproj",
        "VapeCache.Extensions.DependencyInjection/VapeCache.Extensions.DependencyInjection.csproj",
        "VapeCache.Extensions.AspNetCore/VapeCache.Extensions.AspNetCore.csproj",
        "VapeCache.Extensions.Aspire/VapeCache.Extensions.Aspire.csproj"
    )
}

function Assert-ReleasePackageBranding
{
    $expectedAuthors = "DFWFORSALE INC"
    $expectedCompany = "DFWFORSALE INC"
    $requiredCopyrightToken = "DFWFORSALE INC"

    foreach ($project in Get-ReleasePackageProjects)
    {
        $projectPath = Join-Path (Get-ReleaseRepoRoot) $project
        if (-not (Test-Path -LiteralPath $projectPath))
        {
            throw "Release package project not found: $project"
        }

        [xml]$projectXml = Get-Content -LiteralPath $projectPath
        $authorsNode = $projectXml.SelectSingleNode("/Project/PropertyGroup/Authors")
        $companyNode = $projectXml.SelectSingleNode("/Project/PropertyGroup/Company")
        $copyrightNode = $projectXml.SelectSingleNode("/Project/PropertyGroup/Copyright")

        $authors = if ($null -eq $authorsNode) { "" } else { $authorsNode.InnerText.Trim() }
        $company = if ($null -eq $companyNode) { "" } else { $companyNode.InnerText.Trim() }
        $copyright = if ($null -eq $copyrightNode) { "" } else { $copyrightNode.InnerText.Trim() }

        if ($authors -ne $expectedAuthors)
        {
            throw "Authors branding mismatch in $project. Expected '$expectedAuthors' but found '$authors'."
        }

        if ($company -ne $expectedCompany)
        {
            throw "Company branding mismatch in $project. Expected '$expectedCompany' but found '$company'."
        }

        if ([string]::IsNullOrWhiteSpace($copyright) -or $copyright -notlike "*$requiredCopyrightToken*")
        {
            throw "Copyright branding mismatch in $project. Expected to contain '$requiredCopyrightToken' but found '$copyright'."
        }
    }
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
