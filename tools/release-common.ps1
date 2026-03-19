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
