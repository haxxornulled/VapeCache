[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$PackageVersion = "",
    [string]$CommitMessage = "",
    [string[]]$PushRemotes = @("origin", "oss"),
    [switch]$SkipBuild,
    [switch]$SkipPush,
    [switch]$AllowDirty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "release-package-manifest.ps1")

function Get-NextPatchVersion {
    param([Parameter(Mandatory = $true)][string]$CurrentVersion)

    if ($CurrentVersion -notmatch '^(\d+)\.(\d+)\.(\d+)$') {
        throw "Current package version '$CurrentVersion' is not a plain major.minor.patch version. Pass -PackageVersion explicitly."
    }

    $major = [int]$Matches[1]
    $minor = [int]$Matches[2]
    $patch = [int]$Matches[3] + 1
    return "$major.$minor.$patch"
}

function Invoke-Git {
    param([Parameter(Mandatory = $true)][string[]]$Args)

    & git @Args
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Args -join ' ') failed with exit code $LASTEXITCODE."
    }
}

$repoRoot = Get-ReleaseRepoRoot
Set-Location $repoRoot

$branch = (& git branch --show-current).Trim()
if ($LASTEXITCODE -ne 0) {
    throw "Unable to determine current git branch."
}

if ($branch -ne "main") {
    throw "Automation must run from main. Current branch: $branch"
}

$status = (& git status --short)
if ($LASTEXITCODE -ne 0) {
    throw "git status failed."
}

if ($status -and -not $AllowDirty) {
    throw "Working tree is dirty. Commit or stash changes first, or pass -AllowDirty if this is intentional."
}

foreach ($remote in $PushRemotes) {
    $exists = (& git remote) -contains $remote
    if (-not $exists) {
        throw "Required remote not found: $remote"
    }
}

$resolvedVersion = $PackageVersion.Trim()
if ([string]::IsNullOrWhiteSpace($resolvedVersion)) {
    $currentVersion = Resolve-ReleasePackageVersion
    $resolvedVersion = Get-NextPatchVersion -CurrentVersion $currentVersion
}

if ([string]::IsNullOrWhiteSpace($CommitMessage)) {
    $CommitMessage = "chore(release): bump package versions to $resolvedVersion"
}

$bumpScript = Join-Path $PSScriptRoot "bump-package-versions.ps1"
if ($PSCmdlet.ShouldProcess("package projects", "Bump to $resolvedVersion")) {
    & $bumpScript -PackageVersion $resolvedVersion
    if ($LASTEXITCODE -ne 0) {
        throw "bump-package-versions.ps1 failed with exit code $LASTEXITCODE."
    }
}

if (-not $SkipBuild) {
    if ($PSCmdlet.ShouldProcess("solution", "Build VapeCache.slnx ($Configuration)")) {
        & dotnet build "VapeCache.slnx" -c $Configuration
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet build failed with exit code $LASTEXITCODE."
        }
    }
}

$releaseProjects = Get-ReleasePackageProjects
if ($PSCmdlet.ShouldProcess("release package project files", "Stage version bumps")) {
    foreach ($project in $releaseProjects) {
        Invoke-Git -Args @("add", $project)
    }
}

if ($PSCmdlet.ShouldProcess("git", "Commit version bump")) {
    Invoke-Git -Args @("commit", "-m", $CommitMessage)
}

if (-not $SkipPush) {
    foreach ($remote in $PushRemotes) {
        if ($PSCmdlet.ShouldProcess("$remote/main", "Push commit")) {
            Invoke-Git -Args @("push", $remote, "main")
        }
    }
}

Write-Host "Automation complete."
Write-Host "Version: $resolvedVersion"
Write-Host "Commit message: $CommitMessage"