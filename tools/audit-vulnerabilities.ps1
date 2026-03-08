param(
    [string]$Solution = "VapeCache.sln",
    [string[]]$Projects = @(),
    [switch]$IncludePrivateFeeds,
    [switch]$FailOnPrivateFeedAuthError,
    [string]$PublicSource = "https://api.nuget.org/v3/index.json"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

function Resolve-SolutionPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RequestedPath
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath) -and (Test-Path $RequestedPath)) {
        return $RequestedPath
    }

    foreach ($candidate in @("VapeCache.slnx", "VapeCache.sln")) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    throw "Could not find a solution file. Checked '$RequestedPath', 'VapeCache.slnx', and 'VapeCache.sln'."
}

function Invoke-VulnerabilityAudit {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Target
    )

    function Invoke-DotnetList {
        param(
            [Parameter(Mandatory = $true)]
            [string[]]$Arguments
        )

        $output = & dotnet @Arguments 2>&1
        $exitCode = $LASTEXITCODE
        $output | ForEach-Object { Write-Host $_ }

        return @{
            ExitCode = $exitCode
            Output = @($output)
        }
    }

    function Test-IsPrivateFeedAuthFailure {
        param(
            [Parameter(Mandatory = $true)]
            [string[]]$OutputLines
        )

        $text = ($OutputLines | Out-String)
        return $text -match "401 \(Unauthorized\)" `
            -or $text -match "could not be authenticated by the GitHub Packages service" `
            -or $text -match "nuget\.pkg\.github\.com"
    }

    $baseArgs = @("list", $Target, "package", "--vulnerable", "--include-transitive")
    $args = @($baseArgs)
    if (-not $IncludePrivateFeeds) {
        $args += @("--source", $PublicSource)
    }

    Write-Host "Auditing vulnerabilities for: $Target"
    if (-not $IncludePrivateFeeds) {
        Write-Host "  Source mode: public-only ($PublicSource)"
    }
    else {
        Write-Host "  Source mode: configured feeds (private + public)"
    }

    $result = Invoke-DotnetList -Arguments $args
    if ($result.ExitCode -eq 0) {
        return
    }

    if ($IncludePrivateFeeds -and -not $FailOnPrivateFeedAuthError -and (Test-IsPrivateFeedAuthFailure -OutputLines $result.Output)) {
        Write-Warning "Private-feed vulnerability audit failed for $Target due to GitHub Packages auth. Falling back to public feed scan ($PublicSource)."
        $fallbackArgs = @($baseArgs + @("--source", $PublicSource))
        $fallbackResult = Invoke-DotnetList -Arguments $fallbackArgs
        if ($fallbackResult.ExitCode -eq 0) {
            return
        }

        throw "Public-feed fallback vulnerability audit failed for $Target (dotnet exit code: $($fallbackResult.ExitCode))."
    }

    throw "Vulnerability audit failed for $Target (dotnet exit code: $($result.ExitCode))."
}

function Test-ProjectHasPackageReferences {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectPath
    )

    if (-not (Test-Path $ProjectPath)) {
        return $false
    }

    return Select-String -Path $ProjectPath -Pattern "<PackageReference " -Quiet
}

function Get-SolutionProjects {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SolutionPath
    )

    $lines = & dotnet sln $SolutionPath list 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to enumerate projects from solution $SolutionPath"
    }

    $projects = @()
    foreach ($line in $lines) {
        if ($line -match "^\s*$" -or $line -match "^Project\(s\)" -or $line -match "^-+$") {
            continue
        }

        $projects += $line.Trim()
    }

    return $projects
}

$targets = New-Object System.Collections.Generic.List[string]
if ($Projects.Count -gt 0)
{
    foreach ($project in $Projects) {
        if (Test-ProjectHasPackageReferences -ProjectPath $project) {
            $targets.Add($project) | Out-Null
        }
        else {
            Write-Host "Skipping vulnerability audit for $project (no PackageReference items)."
        }
    }
}
else
{
    $resolvedSolution = Resolve-SolutionPath -RequestedPath $Solution
    foreach ($project in (Get-SolutionProjects -SolutionPath $resolvedSolution)) {
        if (Test-ProjectHasPackageReferences -ProjectPath $project) {
            $targets.Add($project) | Out-Null
        }
        else {
            Write-Host "Skipping vulnerability audit for $project (no PackageReference items)."
        }
    }
}

if ($targets.Count -eq 0) {
    throw "No projects with PackageReference entries were found for vulnerability audit."
}

foreach ($target in $targets) {
    Invoke-VulnerabilityAudit -Target $target
}

Write-Host "Vulnerability audit completed successfully."
