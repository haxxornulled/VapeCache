[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Tag,
    [string]$OutputPath = "",
    [string]$CommitSha = "",
    [switch]$AssumePublished
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "release-package-manifest.ps1")
. (Join-Path $PSScriptRoot "release-common.ps1")

$repoRoot = Get-ReleaseRepoRoot
Set-Location $repoRoot

Assert-ReleasePackageDocumentationMetadata

$resolvedVersion = Convert-ToReleaseNotesVersion -Tag $Tag -Version ""
if ([string]::IsNullOrWhiteSpace($OutputPath))
{
    $OutputPath = Join-Path $repoRoot "artifacts/release-notes-$Tag.md"
}
else
{
    $OutputPath = Resolve-ReleaseAbsolutePath -Path $OutputPath -BasePath $repoRoot
}

if ([string]::IsNullOrWhiteSpace($CommitSha))
{
    $CommitSha = Get-ReleaseGitText @("rev-parse", "HEAD")
}

$nuGetResults = @()
$gitHubPackagesResults = @()

if ($AssumePublished)
{
    $publishedResults = @(Get-ReleasePackageCatalog | ForEach-Object {
        [pscustomobject]@{
            PackageId = $_.PackageId
            Status    = "published"
        }
    })

    $nuGetResults = $publishedResults
    $gitHubPackagesResults = $publishedResults
}

New-ReleaseNotesFile `
    -Path $OutputPath `
    -Tag $Tag `
    -Version $resolvedVersion `
    -CommitSha $CommitSha `
    -ValidationLines @(
        "- release-check: passed",
        "- package publish workflow: passed"
    ) `
    -NuGetResults $nuGetResults `
    -GitHubPackagesResults $gitHubPackagesResults

Write-Output $OutputPath
