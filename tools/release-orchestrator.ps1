[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$PackageVersion = "",
    [string]$PackageOutput = "artifacts/release-orchestrator-packages",
    [string]$NuGetSource = "https://api.nuget.org/v3/index.json",
    [string]$NuGetApiKey = $env:NUGET_API_KEY,
    [string]$GitHubPackagesSource = "https://nuget.pkg.github.com/haxxornulled/index.json",
    [string]$GitHubPackagesApiKey = "",
    [string[]]$PushRemotes = @("origin", "oss"),
    [string[]]$ReleaseRepos = @("haxxornulled/VapeCache", "haxxornulled/VapeCache-Enterprise"),
    [string]$TagPrefix = "v",
    [string]$ReleaseNotesFile = "",
    [string]$CommitMessage = "",
    [switch]$SkipReleaseCheck,
    [switch]$SkipPack,
    [switch]$SkipSmoke,
    [switch]$SkipNuGetPublish,
    [switch]$SkipGitHubPackagesPublish,
    [switch]$SkipMainPush,
    [switch]$SkipTag,
    [switch]$SkipTagPush,
    [switch]$SkipGitHubReleases,
    [switch]$AllowDirty,
    [switch]$CommitIfDirty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "release-package-manifest.ps1")

function Write-Section
{
    param([Parameter(Mandatory = $true)][string]$Title)
    Write-Host ""
    Write-Host "==> $Title" -ForegroundColor Cyan
}

function Invoke-Step
{
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    Write-Section $Name
    & $Action
    if ($LASTEXITCODE -ne 0)
    {
        throw "Step failed: $Name (exit code: $LASTEXITCODE)"
    }
}

function Assert-Command
{
    param([Parameter(Mandatory = $true)][string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue))
    {
        throw "Required command not found: $Name"
    }
}

function Get-GitText
{
    param([Parameter(Mandatory = $true)][string[]]$Args)
    $text = (& git @Args).Trim()
    if ($LASTEXITCODE -ne 0)
    {
        throw "git $($Args -join ' ') failed."
    }

    return $text
}

function Resolve-GitHubPackagesKey
{
    param([string]$ExplicitKey)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitKey))
    {
        return $ExplicitKey
    }

    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_PACKAGES_TOKEN))
    {
        return $env:GITHUB_PACKAGES_TOKEN
    }

    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN))
    {
        return $env:GITHUB_TOKEN
    }

    if (Get-Command gh -ErrorAction SilentlyContinue)
    {
        try
        {
            $token = (gh auth token).Trim()
            if (-not [string]::IsNullOrWhiteSpace($token))
            {
                return $token
            }
        }
        catch
        {
            # no-op fallback
        }
    }

    return ""
}

function New-ReleaseNotesFile
{
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Tag,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$CommitSha,
        [object[]]$NuGetResults = @(),
        [object[]]$GitHubPackagesResults = @()
    )

    $nugetSummary = if ($NuGetResults.Count -eq 0)
    {
        "- NuGet publish not executed in this run."
    }
    else
    {
        ($NuGetResults | ForEach-Object { "- $($_.PackageId): $($_.Status)" }) -join [Environment]::NewLine
    }

    $ghSummary = if ($GitHubPackagesResults.Count -eq 0)
    {
        "- GitHub Packages publish not executed in this run."
    }
    else
    {
        ($GitHubPackagesResults | ForEach-Object { "- $($_.PackageId): $($_.Status)" }) -join [Environment]::NewLine
    }

    $content = @"
## VapeCache $Version

Commit: \`$CommitSha\`

### NuGet Publish Status
$nugetSummary

### GitHub Packages Publish Status
$ghSummary

### Validation
- release-check: passed
- package smoke tests: passed
"@

    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent))
    {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    Set-Content -LiteralPath $Path -Value $content -Encoding UTF8
}

function Publish-PackageSet
{
    param(
        [Parameter(Mandatory = $true)][object[]]$Packages,
        [Parameter(Mandatory = $true)][string]$PackageOutput,
        [Parameter(Mandatory = $true)][string]$PackageVersion,
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$ApiKey,
        [Parameter(Mandatory = $true)][string]$FeedName
    )

    $results = New-Object System.Collections.Generic.List[object]

    foreach ($package in $Packages)
    {
        $packageFile = Join-Path $PackageOutput "$($package.PackageId).$PackageVersion.nupkg"
        if (-not (Test-Path -LiteralPath $packageFile))
        {
            throw "Package artifact missing: $packageFile"
        }

        Write-Host "Publishing $($package.PackageId) to $FeedName..."
        $pushOutput = & dotnet nuget push $packageFile --source $Source --api-key $ApiKey --skip-duplicate 2>&1
        $exitCode = $LASTEXITCODE
        $pushText = ($pushOutput | Out-String)

        $status = "unknown"
        if ($exitCode -eq 0 -and $pushText -match "Your package was pushed\.")
        {
            $status = "published"
        }
        elseif ($exitCode -eq 0 -and ($pushText -match "already exists" -or $pushText -match "duplicate"))
        {
            $status = "duplicate"
        }
        elseif ($exitCode -eq 0)
        {
            $status = "ok"
        }
        else
        {
            $status = "failed"
        }

        $results.Add([pscustomobject]@{
                Feed      = $FeedName
                PackageId = $package.PackageId
                Status    = $status
                ExitCode  = $exitCode
                Message   = $pushText.Trim()
            })

        if ($exitCode -ne 0)
        {
            Write-Host $pushText
            throw "Package publish failed for $($package.PackageId) on $FeedName."
        }
    }

    return $results.ToArray()
}

Assert-Command "git"
Assert-Command "dotnet"
Assert-Command "pwsh"

$repoRoot = Get-ReleaseRepoRoot
Set-Location $repoRoot

$packages = Get-ReleasePackageVersionInfo
$resolvedPackageVersion = Resolve-ReleasePackageVersion -PackageVersion $PackageVersion
$tagName = "$TagPrefix$resolvedPackageVersion"
if ([string]::IsNullOrWhiteSpace($CommitMessage))
{
    $CommitMessage = "chore(release): bump package versions to $resolvedPackageVersion"
}

if (-not [System.IO.Path]::IsPathRooted($PackageOutput))
{
    $PackageOutput = Join-Path $repoRoot $PackageOutput
}

if ($SkipTag -and -not $SkipGitHubReleases)
{
    throw "-SkipTag cannot be combined with GitHub release publishing. Pass -SkipGitHubReleases as well."
}

Write-Section "Preflight"
$branch = Get-GitText @("branch", "--show-current")
if ($branch -ne "main")
{
    throw "Release orchestration must run from main. Current branch: $branch"
}

$status = (& git status --short)
if ($LASTEXITCODE -ne 0)
{
    throw "git status failed."
}

if ($status -and -not $AllowDirty -and -not $CommitIfDirty)
{
    throw "Working tree is dirty. Commit or stash changes, or pass -CommitIfDirty / -AllowDirty."
}

foreach ($remote in $PushRemotes)
{
    $exists = (& git remote) -contains $remote
    if (-not $exists)
    {
        throw "Required remote not found: $remote"
    }
}

foreach ($remote in $PushRemotes)
{
    Invoke-Step -Name "Fetch $remote" -Action { git fetch $remote --tags --force }
    $behindCount = [int](Get-GitText @("rev-list", "--count", "HEAD..$remote/main"))
    if ($behindCount -gt 0)
    {
        throw "Local main is behind $remote/main by $behindCount commit(s)."
    }
}

if ($CommitIfDirty -and $status)
{
    Invoke-Step -Name "Commit pending release changes" -Action {
        git add -A
        git commit -m $CommitMessage
    }
}

Assert-ReleasePackageBranding

if (-not $SkipReleaseCheck)
{
    Invoke-Step -Name "Release check (without pack)" -Action {
        pwsh -File (Join-Path $PSScriptRoot "release-check.ps1") -Configuration $Configuration -SkipPack
    }
}

if (-not $SkipPack)
{
    Invoke-Step -Name "Pack release packages" -Action {
        pwsh -File (Join-Path $PSScriptRoot "pack-release-packages.ps1") -OutputDir $PackageOutput -PackageVersion $resolvedPackageVersion
    }
}

if (-not $SkipSmoke)
{
    Invoke-Step -Name "Smoke test VapeCache.Runtime package" -Action {
        pwsh -File (Join-Path $PSScriptRoot "package-smoke.ps1") -PackageOutput $PackageOutput -PackageId "VapeCache.Runtime"
    }
    Invoke-Step -Name "Smoke test VapeCache.Extensions.PubSub package" -Action {
        pwsh -File (Join-Path $PSScriptRoot "package-smoke.ps1") -PackageOutput $PackageOutput -PackageId "VapeCache.Extensions.PubSub"
    }
    Invoke-Step -Name "Smoke test VapeCache.Extensions.Logging package" -Action {
        pwsh -File (Join-Path $PSScriptRoot "package-smoke.ps1") -PackageOutput $PackageOutput -PackageId "VapeCache.Extensions.Logging"
    }
}

$nuGetResults = @()
if (-not $SkipNuGetPublish)
{
    if ([string]::IsNullOrWhiteSpace($NuGetApiKey))
    {
        throw "NuGet publish requested but no API key was found. Set NUGET_API_KEY or pass -NuGetApiKey."
    }

    Write-Section "Publish NuGet.org packages"
    $nuGetResults = Publish-PackageSet `
        -Packages $packages `
        -PackageOutput $PackageOutput `
        -PackageVersion $resolvedPackageVersion `
        -Source $NuGetSource `
        -ApiKey $NuGetApiKey `
        -FeedName "nuget.org"
}

$gitHubPackagesResults = @()
if (-not $SkipGitHubPackagesPublish)
{
    $resolvedGitHubPackagesApiKey = Resolve-GitHubPackagesKey -ExplicitKey $GitHubPackagesApiKey
    if ([string]::IsNullOrWhiteSpace($resolvedGitHubPackagesApiKey))
    {
        throw "GitHub Packages publish requested but no token was found. Pass -GitHubPackagesApiKey or set GITHUB_PACKAGES_TOKEN/GITHUB_TOKEN."
    }

    Write-Section "Publish GitHub Packages"
    $gitHubPackagesResults = Publish-PackageSet `
        -Packages $packages `
        -PackageOutput $PackageOutput `
        -PackageVersion $resolvedPackageVersion `
        -Source $GitHubPackagesSource `
        -ApiKey $resolvedGitHubPackagesApiKey `
        -FeedName "github-packages"
}

if (-not $SkipMainPush)
{
    foreach ($remote in $PushRemotes)
    {
        Invoke-Step -Name "Push main to $remote" -Action { git push $remote main }
    }
}

$headSha = Get-GitText @("rev-parse", "HEAD")

if (-not $SkipTag -and -not (Get-GitText @("tag", "--list", $tagName)))
{
    Invoke-Step -Name "Create tag $tagName" -Action { git tag -a $tagName $headSha -m $tagName }
}

if (-not $SkipTag -and -not $SkipTagPush)
{
    foreach ($remote in $PushRemotes)
    {
        Invoke-Step -Name "Push tag $tagName to $remote" -Action { git push $remote $tagName }
    }
}

if ([string]::IsNullOrWhiteSpace($ReleaseNotesFile))
{
    $ReleaseNotesFile = Join-Path $repoRoot "artifacts/release-notes-$tagName.md"
}
elseif (-not [System.IO.Path]::IsPathRooted($ReleaseNotesFile))
{
    $ReleaseNotesFile = Join-Path $repoRoot $ReleaseNotesFile
}

New-ReleaseNotesFile `
    -Path $ReleaseNotesFile `
    -Tag $tagName `
    -Version $resolvedPackageVersion `
    -CommitSha $headSha `
    -NuGetResults $nuGetResults `
    -GitHubPackagesResults $gitHubPackagesResults

if (-not $SkipGitHubReleases)
{
    Assert-Command "gh"

    foreach ($repo in $ReleaseRepos)
    {
        Write-Section "Publish GitHub release $tagName on $repo"
        & gh release view $tagName --repo $repo *> $null
        $exists = ($LASTEXITCODE -eq 0)

        if ($exists)
        {
            & gh release edit $tagName --repo $repo --title $tagName --notes-file $ReleaseNotesFile --latest
        }
        else
        {
            & gh release create $tagName --repo $repo --title $tagName --notes-file $ReleaseNotesFile --latest
        }

        if ($LASTEXITCODE -ne 0)
        {
            throw "GitHub release publish failed for $repo"
        }
    }
}

$summary = [pscustomobject]@{
    Version                = $resolvedPackageVersion
    Tag                    = $tagName
    Commit                 = $headSha
    NuGetResults           = $nuGetResults
    GitHubPackagesResults  = $gitHubPackagesResults
    ReleaseNotesFile       = $ReleaseNotesFile
}

$summaryPath = Join-Path $repoRoot "artifacts/release-orchestrator-summary-$tagName.json"
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding UTF8

Write-Section "Release Summary"
Write-Host "Version: $resolvedPackageVersion"
Write-Host "Tag: $tagName"
Write-Host "Commit: $headSha"
Write-Host "Summary file: $summaryPath"
Write-Host "Release notes: $ReleaseNotesFile"

if ($nuGetResults.Count -gt 0)
{
    Write-Host ""
    Write-Host "NuGet publish matrix:"
    $nuGetResults | Format-Table Feed, PackageId, Status, ExitCode -AutoSize
}

if ($gitHubPackagesResults.Count -gt 0)
{
    Write-Host ""
    Write-Host "GitHub Packages publish matrix:"
    $gitHubPackagesResults | Format-Table Feed, PackageId, Status, ExitCode -AutoSize
}

Write-Host ""
Write-Host "Release orchestration completed." -ForegroundColor Green
