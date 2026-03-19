function Get-ReleaseRepoRoot
{
    Split-Path $PSScriptRoot -Parent
}

function Get-ReleaseRepositoryUrl
{
    "https://github.com/haxxornulled/VapeCache"
}

function Get-ReleasePackageProjects
{
    @(
        "VapeCache.Core/VapeCache.Core.csproj",
        "VapeCache.Abstractions/VapeCache.Abstractions.csproj",
        "VapeCache.Features.Invalidation/VapeCache.Features.Invalidation.csproj",
        "VapeCache.Infrastructure/VapeCache.Infrastructure.csproj",
        "VapeCache.Extensions.DependencyInjection/VapeCache.Extensions.DependencyInjection.csproj",
        "VapeCache.Extensions.AdminAuth/VapeCache.Extensions.AdminAuth.csproj",
        "VapeCache.Extensions.Logging/VapeCache.Extensions.Logging.csproj",
        "VapeCache.Extensions.PubSub/VapeCache.Extensions.PubSub.csproj",
        "VapeCache.Extensions.Streams/VapeCache.Extensions.Streams.csproj",
        "VapeCache.Extensions.EntityFrameworkCore/VapeCache.Extensions.EntityFrameworkCore.csproj",
        "VapeCache.Extensions.EntityFrameworkCore.OpenTelemetry/VapeCache.Extensions.EntityFrameworkCore.OpenTelemetry.csproj",
        "VapeCache.Extensions.AspNetCore/VapeCache.Extensions.AspNetCore.csproj",
        "VapeCache.Extensions.Aspire/VapeCache.Extensions.Aspire.csproj"
    )
}

function Get-ReleaseSmokePackageIds
{
    @(
        "VapeCache.Runtime",
        "VapeCache.Extensions.DependencyInjection",
        "VapeCache.Extensions.AdminAuth",
        "VapeCache.Extensions.PubSub",
        "VapeCache.Extensions.Logging"
    )
}

function Get-ReleaseProjectPropertyValue
{
    param(
        [Parameter(Mandatory = $true)][xml]$ProjectXml,
        [Parameter(Mandatory = $true)][string]$PropertyName
    )

    $node = $ProjectXml.SelectNodes("/Project/PropertyGroup/$PropertyName") | Select-Object -First 1
    if ($null -eq $node)
    {
        return ""
    }

    return $node.InnerText.Trim()
}

function Get-ReleasePackageMetadata
{
    $repoRoot = Get-ReleaseRepoRoot
    $expectedRepositoryUrl = Get-ReleaseRepositoryUrl

    foreach ($project in Get-ReleasePackageProjects)
    {
        $projectPath = Join-Path $repoRoot $project
        if (-not (Test-Path -LiteralPath $projectPath))
        {
            throw "Release package project not found: $project"
        }

        [xml]$projectXml = Get-Content -LiteralPath $projectPath
        $packageId = Get-ReleaseProjectPropertyValue -ProjectXml $projectXml -PropertyName "PackageId"
        $version = Get-ReleaseProjectPropertyValue -ProjectXml $projectXml -PropertyName "Version"
        $repositoryUrl = Get-ReleaseProjectPropertyValue -ProjectXml $projectXml -PropertyName "RepositoryUrl"
        $packageProjectUrl = Get-ReleaseProjectPropertyValue -ProjectXml $projectXml -PropertyName "PackageProjectUrl"
        $authors = Get-ReleaseProjectPropertyValue -ProjectXml $projectXml -PropertyName "Authors"
        $company = Get-ReleaseProjectPropertyValue -ProjectXml $projectXml -PropertyName "Company"
        $copyright = Get-ReleaseProjectPropertyValue -ProjectXml $projectXml -PropertyName "Copyright"

        if ([string]::IsNullOrWhiteSpace($packageId))
        {
            throw "PackageId not found in $project"
        }

        if ([string]::IsNullOrWhiteSpace($version))
        {
            throw "Version not found in $project"
        }

        if ([string]::IsNullOrWhiteSpace($repositoryUrl))
        {
            throw "RepositoryUrl not found in $project"
        }

        if ($repositoryUrl -ne $expectedRepositoryUrl)
        {
            throw "RepositoryUrl mismatch in $project. Expected '$expectedRepositoryUrl' but found '$repositoryUrl'."
        }

        if ([string]::IsNullOrWhiteSpace($packageProjectUrl))
        {
            throw "PackageProjectUrl not found in $project"
        }

        if ($packageProjectUrl -ne $expectedRepositoryUrl)
        {
            throw "PackageProjectUrl mismatch in $project. Expected '$expectedRepositoryUrl' but found '$packageProjectUrl'."
        }

        [pscustomobject]@{
            Project           = $project
            ProjectPath       = $projectPath
            PackageId         = $packageId
            Version           = $version
            RepositoryUrl     = $repositoryUrl
            PackageProjectUrl = $packageProjectUrl
            Authors           = $authors
            Company           = $company
            Copyright         = $copyright
        }
    }
}

function Assert-ReleasePackageBranding
{
    $expectedAuthors = "DFWFORSALE INC"
    $expectedCompany = "DFWFORSALE INC"
    $requiredCopyrightToken = "DFWFORSALE INC"

    foreach ($package in Get-ReleasePackageMetadata)
    {
        if ($package.Authors -ne $expectedAuthors)
        {
            throw "Authors branding mismatch in $($package.Project). Expected '$expectedAuthors' but found '$($package.Authors)'."
        }

        if ($package.Company -ne $expectedCompany)
        {
            throw "Company branding mismatch in $($package.Project). Expected '$expectedCompany' but found '$($package.Company)'."
        }

        if ([string]::IsNullOrWhiteSpace($package.Copyright) -or $package.Copyright -notlike "*$requiredCopyrightToken*")
        {
            throw "Copyright branding mismatch in $($package.Project). Expected to contain '$requiredCopyrightToken' but found '$($package.Copyright)'."
        }
    }

    Assert-ApplicationLayerPackagingBoundary
}

function Assert-ApplicationLayerPackagingBoundary
{
    $repoRoot = Get-ReleaseRepoRoot
    $applicationProjectPath = Join-Path $repoRoot "VapeCache.Application/VapeCache.Application.csproj"
    if (-not (Test-Path -LiteralPath $applicationProjectPath))
    {
        throw "Application project not found at expected path: $applicationProjectPath"
    }

    [xml]$applicationProjectXml = Get-Content -LiteralPath $applicationProjectPath
    $applicationIsPackable = Get-ReleaseProjectPropertyValue -ProjectXml $applicationProjectXml -PropertyName "IsPackable"
    if ($applicationIsPackable -eq "true")
    {
        throw "Clean architecture boundary violation: VapeCache.Application must remain non-packable."
    }

    foreach ($project in Get-ReleasePackageProjects)
    {
        $projectPath = Join-Path $repoRoot $project
        [xml]$projectXml = Get-Content -LiteralPath $projectPath
        $projectReferences = $projectXml.SelectNodes("/Project/ItemGroup/ProjectReference")
        foreach ($projectReference in $projectReferences)
        {
            $include = [string]$projectReference.Include
            if ([string]::IsNullOrWhiteSpace($include))
            {
                continue
            }

            if ($include -match "(^|[\\/])VapeCache\.Application([\\/])VapeCache\.Application\.csproj$")
            {
                throw "Clean architecture boundary violation: release package project '$project' references VapeCache.Application."
            }
        }
    }
}

function Get-ReleasePackageVersionInfo
{
    $infos = foreach ($package in Get-ReleasePackageMetadata)
    {
        [pscustomobject]@{
            Project           = $package.Project
            PackageId         = $package.PackageId
            Version           = $package.Version
            RepositoryUrl     = $package.RepositoryUrl
            PackageProjectUrl = $package.PackageProjectUrl
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
