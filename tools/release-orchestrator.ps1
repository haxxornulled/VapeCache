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
. (Join-Path $PSScriptRoot "release-common.ps1")

Assert-ReleaseCommand "git"
Assert-ReleaseCommand "dotnet"
Resolve-ReleasePowerShellCommand | Out-Null

$repoRoot = Get-ReleaseRepoRoot
Set-Location $repoRoot

$packages = Get-ReleasePackageVersionInfo
$resolvedPackageVersion = Resolve-ReleasePackageVersion -PackageVersion $PackageVersion
$tagName = "$TagPrefix$resolvedPackageVersion"
if ([string]::IsNullOrWhiteSpace($CommitMessage))
{
    $CommitMessage = "chore(release): bump package versions to $resolvedPackageVersion"
}

$PackageOutput = Resolve-ReleaseAbsolutePath -Path $PackageOutput -BasePath $repoRoot

if ($SkipTag -and -not $SkipGitHubReleases)
{
    throw "-SkipTag cannot be combined with GitHub release publishing. Pass -SkipGitHubReleases as well."
}

Write-ReleaseSection "Preflight"
$branch = Get-ReleaseGitText @("branch", "--show-current")
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
    Invoke-ReleaseStep -Name "Fetch $remote" -Action { git fetch $remote --tags --force }
    $behindCount = [int](Get-ReleaseGitText @("rev-list", "--count", "HEAD..$remote/main"))
    if ($behindCount -gt 0)
    {
        throw "Local main is behind $remote/main by $behindCount commit(s)."
    }
}

if ($CommitIfDirty -and $status)
{
    Invoke-ReleaseStep -Name "Commit pending release changes" -Action {
        git add -A
        git commit -m $CommitMessage
    }
}

Assert-ReleasePackageBranding

if (-not $SkipReleaseCheck)
{
    Invoke-ReleaseStep -Name "Release check (without pack)" -Action {
        Invoke-ReleaseScript -ScriptPath (Join-Path $PSScriptRoot "release-check.ps1") -ArgumentList @(
            "-Configuration", $Configuration,
            "-SkipPack"
        )
    }
}

if (-not $SkipPack)
{
    Invoke-ReleaseStep -Name "Pack release packages" -Action {
        Invoke-ReleaseScript -ScriptPath (Join-Path $PSScriptRoot "pack-release-packages.ps1") -ArgumentList @(
            "-Configuration", $Configuration,
            "-OutputDir", $PackageOutput,
            "-PackageVersion", $resolvedPackageVersion
        )
    }
}

if (-not $SkipSmoke)
{
    foreach ($packageId in Get-ReleaseSmokePackageIds)
    {
        Invoke-ReleaseStep -Name "Package smoke test ($packageId)" -Action {
            Invoke-ReleaseScript -ScriptPath (Join-Path $PSScriptRoot "package-smoke.ps1") -ArgumentList @(
                "-Configuration", $Configuration,
                "-PackageOutput", $PackageOutput,
                "-PackageId", $packageId
            )
        }
    }
}

$nuGetResults = @()
if (-not $SkipNuGetPublish)
{
    if ([string]::IsNullOrWhiteSpace($NuGetApiKey))
    {
        throw "NuGet publish requested but no API key was found. Set NUGET_API_KEY or pass -NuGetApiKey."
    }

    Write-ReleaseSection "Publish NuGet.org packages"
    $nuGetResults = Publish-ReleasePackageSet `
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

    Write-ReleaseSection "Publish GitHub Packages"
    $gitHubPackagesResults = Publish-ReleasePackageSet `
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
        Invoke-ReleaseStep -Name "Push main to $remote" -Action { git push $remote main }
    }
}

$headSha = Get-ReleaseGitText @("rev-parse", "HEAD")

if (-not $SkipTag -and -not (Get-ReleaseGitText @("tag", "--list", $tagName)))
{
    Invoke-ReleaseStep -Name "Create tag $tagName" -Action { git tag -a $tagName $headSha -m $tagName }
}

if (-not $SkipTag -and -not $SkipTagPush)
{
    foreach ($remote in $PushRemotes)
    {
        Invoke-ReleaseStep -Name "Push tag $tagName to $remote" -Action { git push $remote $tagName }
    }
}

if ([string]::IsNullOrWhiteSpace($ReleaseNotesFile))
{
    $ReleaseNotesFile = Join-Path $repoRoot "artifacts/release-notes-$tagName.md"
}
else
{
    $ReleaseNotesFile = Resolve-ReleaseAbsolutePath -Path $ReleaseNotesFile -BasePath $repoRoot
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
    Assert-ReleaseCommand "gh"

    foreach ($repo in $ReleaseRepos)
    {
        Write-ReleaseSection "Publish GitHub release $tagName on $repo"
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

Write-ReleaseSection "Release Summary"
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
