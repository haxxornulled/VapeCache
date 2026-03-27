[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageVersion,
    [string]$PackageReleaseNotes = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "release-package-manifest.ps1")

function Normalize-PackageVersion {
    param([Parameter(Mandatory = $true)][string]$Version)

    $normalized = $Version.Trim()
    if ($normalized.StartsWith("v", [System.StringComparison]::OrdinalIgnoreCase)) {
        $normalized = $normalized.Substring(1)
    }

    if ($normalized -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z\.-]*)?$') {
        throw "PackageVersion must be SemVer-like (for example 1.2.16 or 1.2.16-rc1). Received '$Version'."
    }

    return $normalized
}

function Set-ProjectPropertyValue {
    param(
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][string]$PropertyName,
        [Parameter(Mandatory = $true)][string]$Value
    )

    $escapedValue = [System.Security.SecurityElement]::Escape($Value)
    $pattern = "(?s)(<$PropertyName>)(.*?)(</$PropertyName>)"
    if ($Content -notmatch $pattern) {
        throw "Property <$PropertyName> was not found."
    }

    $evaluator = [System.Text.RegularExpressions.MatchEvaluator]{
        param($match)
        return $match.Groups[1].Value + $escapedValue + $match.Groups[3].Value
    }

    return [System.Text.RegularExpressions.Regex]::Replace(
        $Content,
        $pattern,
        $evaluator,
        1)
}

$repoRoot = Get-ReleaseRepoRoot
$resolvedVersion = Normalize-PackageVersion -Version $PackageVersion
$packageProjects = Get-ReleasePackageProjects
$updated = New-Object System.Collections.Generic.List[object]

foreach ($project in $packageProjects) {
    $projectPath = Join-Path $repoRoot $project
    $content = Get-Content -LiteralPath $projectPath -Raw
    $updatedContent = Set-ProjectPropertyValue -Content $content -PropertyName "Version" -Value $resolvedVersion

    if ($PSBoundParameters.ContainsKey("PackageReleaseNotes") -and $updatedContent.Contains("<PackageReleaseNotes>")) {
        $updatedContent = Set-ProjectPropertyValue -Content $updatedContent -PropertyName "PackageReleaseNotes" -Value $PackageReleaseNotes
    }

    if ($updatedContent -ne $content) {
        if ($PSCmdlet.ShouldProcess($project, "Set package version to $resolvedVersion")) {
            Set-Content -LiteralPath $projectPath -Value $updatedContent -Encoding UTF8
        }

        $updated.Add([pscustomobject]@{
                Project = $project
                Version = $resolvedVersion
            }) | Out-Null
    }
}

if ($updated.Count -eq 0) {
    Write-Host "No package project files changed."
    return
}

Write-Host "Updated package versions:"
$updated | Sort-Object Project | Format-Table Project, Version -AutoSize
