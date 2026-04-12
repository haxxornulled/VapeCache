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
        "VapeCache.Features.Search/VapeCache.Features.Search.csproj",
        "VapeCache.Infrastructure/VapeCache.Infrastructure.csproj",
        "VapeCache.Extensions.DependencyInjection/VapeCache.Extensions.DependencyInjection.csproj",
        "VapeCache.Extensions.KeyDB/VapeCache.Extensions.KeyDB.csproj",
        "VapeCache.Extensions.AdminAuth/VapeCache.Extensions.AdminAuth.csproj",
        "VapeCache.Extensions.DistributedCache/VapeCache.Extensions.DistributedCache.csproj",
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
        "VapeCache.Extensions.KeyDB",
        "VapeCache.Extensions.AdminAuth",
        "VapeCache.Extensions.DistributedCache",
        "VapeCache.Extensions.PubSub",
        "VapeCache.Extensions.Logging"
    )
}

function Convert-ToReleaseRepositoryFileUrl
{
    param(
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    $normalized = $RelativePath.Replace("\", "/").TrimStart("/")
    "$(Get-ReleaseRepositoryUrl)/blob/main/$normalized"
}

function Get-ReleasePackageCatalog
{
    $entries = @(
        @{
            Project = "VapeCache.Core/VapeCache.Core.csproj"
            PackageId = "VapeCache.Core"
            Summary = "Shared primitives package used by other VapeCache packages."
            DocsLabel = "Package matrix"
            DocsPath = "docs/NUGET_PACKAGES.md"
            ReleaseHighlight = ""
        }
        @{
            Project = "VapeCache.Abstractions/VapeCache.Abstractions.csproj"
            PackageId = "VapeCache.Abstractions"
            Summary = "Public contracts, interfaces, and configuration types for VapeCache."
            DocsLabel = "API reference"
            DocsPath = "docs/API_REFERENCE.md"
            ReleaseHighlight = ""
        }
        @{
            Project = "VapeCache.Features.Invalidation/VapeCache.Features.Invalidation.csproj"
            PackageId = "VapeCache.Features.Invalidation"
            Summary = "Optional invalidation policy package for keys, tags, and zones."
            DocsLabel = "Cache invalidation"
            DocsPath = "docs/CACHE_INVALIDATION.md"
            ReleaseHighlight = ""
        }
        @{
            Project = "VapeCache.Features.Search/VapeCache.Features.Search.csproj"
            PackageId = "VapeCache.Features.Search"
            Summary = "Typed RediSearch projection, query, and invalidation helpers for HASH-backed operational search."
            DocsLabel = "Redis search guide"
            DocsPath = "docs/REDIS_SEARCH.md"
            ReleaseHighlight = "Enterprise pattern: use denormalized HASH projections plus RediSearch indexes, then invalidate hot result pages with search tags/zones instead of flattening the whole runtime behind a generic search abstraction."
        }
        @{
            Project = "VapeCache.Infrastructure/VapeCache.Infrastructure.csproj"
            PackageId = "VapeCache.Runtime"
            Summary = "Core Redis-first runtime package with transport, fallback behavior, and telemetry."
            DocsLabel = "Quick start"
            DocsPath = "docs/QUICKSTART.md"
            ReleaseHighlight = ""
        }
        @{
            Project = "VapeCache.Extensions.DependencyInjection/VapeCache.Extensions.DependencyInjection.csproj"
            PackageId = "VapeCache.Extensions.DependencyInjection"
            Summary = "One-call IServiceCollection facade for hybrid Redis and in-memory-only VapeCache registration."
            DocsLabel = "Configuration"
            DocsPath = "docs/CONFIGURATION.md"
            ReleaseHighlight = ""
        }
        @{
            Project = "VapeCache.Extensions.KeyDB/VapeCache.Extensions.KeyDB.csproj"
            PackageId = "VapeCache.Extensions.KeyDB"
            Summary = "Explicit KeyDB registration facade with default KeyDbConnection section binding."
            DocsLabel = "Package readme"
            DocsPath = "VapeCache.Extensions.KeyDB/README.md"
            ReleaseHighlight = "Backend boundary package: keeps KeyDB intent explicit at registration while reusing the shared Redis-protocol runtime."
        }
        @{
            Project = "VapeCache.Extensions.AdminAuth/VapeCache.Extensions.AdminAuth.csproj"
            PackageId = "VapeCache.Extensions.AdminAuth"
            Summary = "Reusable admin authentication and authorization wiring for VapeCache hosts."
            DocsLabel = "Admin auth"
            DocsPath = "docs/ADMIN_AUTH.md"
            ReleaseHighlight = ""
        }
        @{
            Project = "VapeCache.Extensions.DistributedCache/VapeCache.Extensions.DistributedCache.csproj"
            PackageId = "VapeCache.Extensions.DistributedCache"
            Summary = "IDistributedCache and IBufferDistributedCache bridge for interoperability and migration."
            DocsLabel = "Distributed cache bridge"
            DocsPath = "docs/DISTRIBUTED_CACHE_BRIDGE.md"
            ReleaseHighlight = "Compatibility contract: implements the public Microsoft distributed-cache APIs and preserves caller-visible expiration semantics; does not promise backend storage-format parity."
        }
        @{
            Project = "VapeCache.Extensions.Logging/VapeCache.Extensions.Logging.csproj"
            PackageId = "VapeCache.Extensions.Logging"
            Summary = "Centralized Serilog and OpenTelemetry logging wiring for VapeCache hosts."
            DocsLabel = "Logging and telemetry"
            DocsPath = "docs/LOGGING_TELEMETRY_CONFIGURATION.md"
            ReleaseHighlight = ""
        }
        @{
            Project = "VapeCache.Extensions.PubSub/VapeCache.Extensions.PubSub.csproj"
            PackageId = "VapeCache.Extensions.PubSub"
            Summary = "Optional Redis pub/sub package with bounded delivery queues and reconnect behavior."
            DocsLabel = "Configuration"
            DocsPath = "docs/CONFIGURATION.md"
            ReleaseHighlight = ""
        }
        @{
            Project = "VapeCache.Extensions.Streams/VapeCache.Extensions.Streams.csproj"
            PackageId = "VapeCache.Extensions.Streams"
            Summary = "Optional Redis Streams package with Redis 8.6 idempotent producer support."
            DocsLabel = "Redis protocol support"
            DocsPath = "docs/REDIS_PROTOCOL_SUPPORT.md"
            ReleaseHighlight = ""
        }
        @{
            Project = "VapeCache.Extensions.EntityFrameworkCore/VapeCache.Extensions.EntityFrameworkCore.csproj"
            PackageId = "VapeCache.Extensions.EntityFrameworkCore"
            Summary = "EF Core adapter package for second-level cache interception and invalidation wiring."
            DocsLabel = "EF Core second-level cache"
            DocsPath = "docs/EFCORE_SECOND_LEVEL_CACHE.md"
            ReleaseHighlight = ""
        }
        @{
            Project = "VapeCache.Extensions.EntityFrameworkCore.OpenTelemetry/VapeCache.Extensions.EntityFrameworkCore.OpenTelemetry.csproj"
            PackageId = "VapeCache.Extensions.EntityFrameworkCore.OpenTelemetry"
            Summary = "OpenTelemetry metrics and activity hooks for EF Core cache interceptor events."
            DocsLabel = "EF Core second-level cache"
            DocsPath = "docs/EFCORE_SECOND_LEVEL_CACHE.md"
            ReleaseHighlight = ""
        }
        @{
            Project = "VapeCache.Extensions.AspNetCore/VapeCache.Extensions.AspNetCore.csproj"
            PackageId = "VapeCache.Extensions.AspNetCore"
            Summary = "ASP.NET Core output-cache integration backed by VapeCache."
            DocsLabel = "ASP.NET Core pipeline"
            DocsPath = "docs/ASPNETCORE_PIPELINE_CACHING.md"
            ReleaseHighlight = ""
        }
        @{
            Project = "VapeCache.Extensions.Aspire/VapeCache.Extensions.Aspire.csproj"
            PackageId = "VapeCache.Extensions.Aspire"
            Summary = ".NET Aspire integration for service discovery, health checks, telemetry, and endpoints."
            DocsLabel = "Aspire integration"
            DocsPath = "docs/ASPIRE_INTEGRATION.md"
            ReleaseHighlight = ""
        }
    )

    foreach ($entry in $entries)
    {
        $packageId = [string]$entry.PackageId
        $docsPath = [string]$entry.DocsPath

        [pscustomobject]@{
            Project            = [string]$entry.Project
            PackageId          = $packageId
            Summary            = [string]$entry.Summary
            DocsLabel          = [string]$entry.DocsLabel
            DocsPath           = $docsPath
            DocsUrl            = Convert-ToReleaseRepositoryFileUrl -RelativePath $docsPath
            NuGetUrl           = "https://www.nuget.org/packages/$packageId"
            GitHubPackagesUrl  = "https://github.com/users/haxxornulled/packages/nuget/package/$($packageId.ToLowerInvariant())"
            ReleaseHighlight   = [string]$entry.ReleaseHighlight
        }
    }
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

function Assert-ReleasePackageDocumentationMetadata
{
    $repoRoot = Get-ReleaseRepoRoot
    $catalog = @(Get-ReleasePackageCatalog)
    $releaseProjects = @(Get-ReleasePackageProjects | Sort-Object)
    $catalogProjects = @($catalog.Project | Sort-Object)

    $projectDiff = Compare-Object -ReferenceObject $releaseProjects -DifferenceObject $catalogProjects
    if ($projectDiff)
    {
        $details = $projectDiff |
            ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" }
        throw "Release package catalog does not match release package projects: $($details -join ', ')"
    }

    foreach ($entry in $catalog)
    {
        if ([string]::IsNullOrWhiteSpace($entry.Summary))
        {
            throw "Release package catalog summary is required for $($entry.PackageId)."
        }

        if ([string]::IsNullOrWhiteSpace($entry.DocsLabel))
        {
            throw "Release package catalog docs label is required for $($entry.PackageId)."
        }

        if ([string]::IsNullOrWhiteSpace($entry.DocsPath))
        {
            throw "Release package catalog docs path is required for $($entry.PackageId)."
        }

        $docsPath = Join-Path $repoRoot $entry.DocsPath
        if (-not (Test-Path -LiteralPath $docsPath))
        {
            throw "Release package docs path not found for $($entry.PackageId): $($entry.DocsPath)"
        }

        $projectPath = Join-Path $repoRoot $entry.Project
        [xml]$projectXml = Get-Content -LiteralPath $projectPath
        $packageReadmeFile = Get-ReleaseProjectPropertyValue -ProjectXml $projectXml -PropertyName "PackageReadmeFile"
        if ([string]::IsNullOrWhiteSpace($packageReadmeFile))
        {
            throw "PackageReadmeFile is required for $($entry.Project)."
        }

        $projectDirectory = Split-Path -Path $projectPath -Parent
        $candidatePaths = @(
            (Join-Path $projectDirectory $packageReadmeFile),
            (Join-Path $repoRoot $packageReadmeFile)
        ) | Select-Object -Unique

        $resolvedReadme = $candidatePaths | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        if ([string]::IsNullOrWhiteSpace($resolvedReadme))
        {
            throw "Package readme '$packageReadmeFile' was not found for $($entry.Project)."
        }
    }
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
