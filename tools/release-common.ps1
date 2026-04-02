function Write-ReleaseSection
{
    param([Parameter(Mandatory = $true)][string]$Title)

    Write-Host ""
    Write-Host "==> $Title" -ForegroundColor Cyan
}

function Invoke-ReleaseStep
{
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    Write-ReleaseSection $Name
    Remove-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
    & $Action
    if ($null -ne $global:LASTEXITCODE -and $global:LASTEXITCODE -ne 0)
    {
        throw "Step failed: $Name (exit code: $global:LASTEXITCODE)"
    }
}

function Assert-ReleaseCommand
{
    param([Parameter(Mandatory = $true)][string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue))
    {
        throw "Required command not found: $Name"
    }
}

function Resolve-ReleasePowerShellCommand
{
    foreach ($candidate in @("pwsh", "powershell", "powershell.exe"))
    {
        $command = Get-Command $candidate -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $command)
        {
            return $command.Source
        }
    }

    throw "Required command not found: pwsh or powershell."
}

function Invoke-ReleaseScript
{
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [string[]]$ArgumentList = @()
    )

    $powerShellCommand = Resolve-ReleasePowerShellCommand
    & $powerShellCommand -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @ArgumentList
}

function Resolve-ReleaseAbsolutePath
{
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$BasePath = (Get-ReleaseRepoRoot)
    )

    if ([System.IO.Path]::IsPathRooted($Path))
    {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $BasePath $Path))
}

function Get-ReleaseGitText
{
    param([Parameter(Mandatory = $true)][string[]]$Args)

    $raw = & git @Args
    if ($LASTEXITCODE -ne 0)
    {
        throw "git $($Args -join ' ') failed."
    }

    if ($null -eq $raw)
    {
        return ""
    }

    return ($raw | Out-String).Trim()
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

function Convert-ToReleaseNotesVersion
{
    param(
        [string]$Tag,
        [string]$Version
    )

    if (-not [string]::IsNullOrWhiteSpace($Version))
    {
        return $Version.Trim()
    }

    $normalizedTag = [string]$Tag
    if ($normalizedTag.StartsWith("v", [System.StringComparison]::OrdinalIgnoreCase))
    {
        return $normalizedTag.Substring(1)
    }

    return $normalizedTag
}

function Convert-ToReleaseMarkdownCell
{
    param([AllowNull()][string]$Text)

    if ([string]::IsNullOrEmpty($Text))
    {
        return ""
    }

    return $Text.Replace("|", "\|").Replace("`r", "").Replace("`n", "<br/>")
}

function New-ReleaseNotesFile
{
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Tag,
        [string]$Version = "",
        [Parameter(Mandatory = $true)][string]$CommitSha,
        [string[]]$ValidationLines = @(
            "- release-check: passed",
            "- package publish workflow: passed"
        ),
        [object[]]$NuGetResults = @(),
        [object[]]$GitHubPackagesResults = @()
    )

    $resolvedVersion = Convert-ToReleaseNotesVersion -Tag $Tag -Version $Version
    $catalog = @(Get-ReleasePackageCatalog | Sort-Object PackageId)

    $packageMatrix = @(
        "| Package | Summary | NuGet | GitHub Packages | Docs |",
        "| --- | --- | --- | --- | --- |"
    )

    foreach ($entry in $catalog)
    {
        $summary = Convert-ToReleaseMarkdownCell -Text $entry.Summary
        $packageMatrix += "| ``$($entry.PackageId)`` | $summary | [NuGet]($($entry.NuGetUrl)) | [GitHub Packages]($($entry.GitHubPackagesUrl)) | [$($entry.DocsLabel)]($($entry.DocsUrl)) |"
    }

    $releaseHighlights = @(
        $catalog |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_.ReleaseHighlight) } |
            ForEach-Object {
                $highlight = Convert-ToReleaseMarkdownCell -Text $_.ReleaseHighlight
                "- ``$($_.PackageId)``: $highlight"
            }
    )

    $nugetSummary = if ($NuGetResults.Count -eq 0)
    {
        @("- NuGet publish not executed in this run.")
    }
    else
    {
        @(
            $catalog | ForEach-Object {
                $packageId = $_.PackageId
                $match = $NuGetResults | Where-Object { $_.PackageId -eq $packageId } | Select-Object -First 1
                $status = if ($null -eq $match) { "not-run" } else { $match.Status }
                "- [$packageId]($($_.NuGetUrl)): $status"
            }
        )
    }

    $ghSummary = if ($GitHubPackagesResults.Count -eq 0)
    {
        @("- GitHub Packages publish not executed in this run.")
    }
    else
    {
        @(
            $catalog | ForEach-Object {
                $packageId = $_.PackageId
                $match = $GitHubPackagesResults | Where-Object { $_.PackageId -eq $packageId } | Select-Object -First 1
                $status = if ($null -eq $match) { "not-run" } else { $match.Status }
                "- [$packageId]($($_.GitHubPackagesUrl)): $status"
            }
        )
    }

    $lines = @(
        "## VapeCache $resolvedVersion",
        "",
        "Commit: ``$CommitSha``",
        "",
        "Package docs, release notes, and feed links are aligned for this release.",
        "",
        "### Package Matrix"
    )

    $lines += $packageMatrix

    if ($releaseHighlights.Count -gt 0)
    {
        $lines += @(
            "",
            "### Compatibility Notes"
        )
        $lines += $releaseHighlights
    }

    $lines += @(
        "",
        "### NuGet Publish Status"
    )
    $lines += $nugetSummary
    $lines += @(
        "",
        "### GitHub Packages Publish Status"
    )
    $lines += $ghSummary

    if ($ValidationLines.Count -gt 0)
    {
        $lines += @(
            "",
            "### Validation"
        )
        $lines += $ValidationLines
    }

    $content = $lines -join [Environment]::NewLine

    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent))
    {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    Set-Content -LiteralPath $Path -Value $content -Encoding UTF8
}

function Get-ReleasePackageArtifacts
{
    param(
        [Parameter(Mandatory = $true)][object[]]$Packages,
        [Parameter(Mandatory = $true)][string]$PackageOutput,
        [Parameter(Mandatory = $true)][string]$PackageVersion
    )

    foreach ($package in $Packages)
    {
        $packageFile = Join-Path $PackageOutput "$($package.PackageId).$PackageVersion.nupkg"
        if (-not (Test-Path -LiteralPath $packageFile))
        {
            throw "Package artifact missing: $packageFile"
        }

        [pscustomobject]@{
            PackageId   = $package.PackageId
            PackageFile = $packageFile
        }
    }
}

function Publish-ReleasePackageSet
{
    param(
        [Parameter(Mandatory = $true)][object[]]$Packages,
        [Parameter(Mandatory = $true)][string]$PackageOutput,
        [Parameter(Mandatory = $true)][string]$PackageVersion,
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$ApiKey,
        [Parameter(Mandatory = $true)][string]$FeedName,
        [string[]]$SkipPackageIds = @()
    )

    $results = New-Object System.Collections.Generic.List[object]

    foreach ($artifact in Get-ReleasePackageArtifacts -Packages $Packages -PackageOutput $PackageOutput -PackageVersion $PackageVersion)
    {
        if ($SkipPackageIds -contains $artifact.PackageId)
        {
            Write-Host "Skipping package publish for $($artifact.PackageId) (configured skip list)."
            $results.Add([pscustomobject]@{
                    Feed      = $FeedName
                    PackageId = $artifact.PackageId
                    Status    = "skipped"
                    ExitCode  = 0
                    Message   = "Skipped by configuration."
                })
            continue
        }

        Write-Host "Publishing $($artifact.PackageId) to $FeedName..."
        $pushOutput = & dotnet nuget push $artifact.PackageFile --source $Source --api-key $ApiKey --skip-duplicate 2>&1
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
                PackageId = $artifact.PackageId
                Status    = $status
                ExitCode  = $exitCode
                Message   = $pushText.Trim()
            })

        if ($exitCode -ne 0)
        {
            Write-Host $pushText
            throw "Package publish failed for $($artifact.PackageId) on $FeedName."
        }
    }

    return $results.ToArray()
}
