param(
    [string]$PackageOutput = "artifacts/packages",
    [string]$PackageId = "VapeCache",
    [string[]]$AdditionalPackageSources = @(),
    [string]$GitHubPackagesSource = "https://nuget.pkg.github.com/haxxornulled/index.json",
    [string]$GitHubPackagesUser = "",
    [string]$GitHubPackagesToken = ""
)

$ErrorActionPreference = "Stop"

$resolvedPackageOutput = (Resolve-Path -LiteralPath $PackageOutput).Path
$resolvedGitHubPackagesSource = if ([string]::IsNullOrWhiteSpace($GitHubPackagesSource)) { "" } else { $GitHubPackagesSource.Trim() }
$resolvedGitHubPackagesToken = if (-not [string]::IsNullOrWhiteSpace($GitHubPackagesToken)) { $GitHubPackagesToken.Trim() } elseif (-not [string]::IsNullOrWhiteSpace($env:GITHUB_PACKAGES_TOKEN)) { $env:GITHUB_PACKAGES_TOKEN.Trim() } elseif (-not [string]::IsNullOrWhiteSpace($env:VAPECACHE_LICENSING_PACKAGES_TOKEN)) { $env:VAPECACHE_LICENSING_PACKAGES_TOKEN.Trim() } elseif (-not [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) { $env:GITHUB_TOKEN.Trim() } else { "" }
$resolvedGitHubPackagesUser = if (-not [string]::IsNullOrWhiteSpace($GitHubPackagesUser)) { $GitHubPackagesUser.Trim() } elseif (-not [string]::IsNullOrWhiteSpace($env:GITHUB_PACKAGES_USER)) { $env:GITHUB_PACKAGES_USER.Trim() } elseif (-not [string]::IsNullOrWhiteSpace($env:GITHUB_ACTOR)) { $env:GITHUB_ACTOR.Trim() } else { "" }

$packagePattern = "^" + [regex]::Escape($PackageId) + "\.(?<version>.+)$"

$package = Get-ChildItem -LiteralPath $resolvedPackageOutput -Filter *.nupkg |
    Where-Object {
        $_.Name -notlike "*.symbols.nupkg" -and
        [regex]::IsMatch($_.BaseName, $packagePattern)
    } |
    Sort-Object Name |
    Select-Object -First 1

if ($null -eq $package)
{
    throw "No package matching $PackageId was found in $resolvedPackageOutput."
}

$versionMatch = [regex]::Match($package.BaseName, $packagePattern)
if (-not $versionMatch.Success)
{
    throw "Unable to determine package version from $($package.Name)."
}

$version = $versionMatch.Groups["version"].Value
$smokeRoot = Join-Path (Split-Path -Path $resolvedPackageOutput -Parent) "package-smoke"
$packageCacheRoot = Join-Path $smokeRoot ".packages"
$nugetConfigPath = Join-Path $smokeRoot "NuGet.Config"
$packageSources = [System.Collections.Generic.List[string]]::new()
$packageSources.Add($resolvedPackageOutput)

foreach ($source in $AdditionalPackageSources)
{
    if ([string]::IsNullOrWhiteSpace($source))
    {
        continue
    }

    if (Test-Path -LiteralPath $source)
    {
        $packageSources.Add((Resolve-Path -LiteralPath $source).Path)
        continue
    }

    $packageSources.Add($source.Trim())
}

$packageSources.Add("https://api.nuget.org/v3/index.json")

if (-not [string]::IsNullOrWhiteSpace($resolvedGitHubPackagesToken))
{
    if ([string]::IsNullOrWhiteSpace($resolvedGitHubPackagesSource))
    {
        throw "GitHub Packages token was provided but GitHubPackagesSource is empty."
    }

    if ([string]::IsNullOrWhiteSpace($resolvedGitHubPackagesUser))
    {
        throw "GitHub Packages token was provided but GitHubPackagesUser could not be resolved."
    }

    if (-not ($packageSources -contains $resolvedGitHubPackagesSource))
    {
        $packageSources.Add($resolvedGitHubPackagesSource)
    }
}

$packageSourceEntries = for ($index = 0; $index -lt $packageSources.Count; $index++)
{
    $source = $packageSources[$index]
    "    <add key=""source$index"" value=""$source"" />"
}

$githubPackagesCredentialsBlock = ""
if (-not [string]::IsNullOrWhiteSpace($resolvedGitHubPackagesToken))
{
    $githubSourceIndex = -1
    for ($index = 0; $index -lt $packageSources.Count; $index++)
    {
        if ($packageSources[$index] -eq $resolvedGitHubPackagesSource)
        {
            $githubSourceIndex = $index
            break
        }
    }

    if ($githubSourceIndex -lt 0)
    {
        throw "Failed to map GitHub Packages source '$resolvedGitHubPackagesSource' in generated NuGet.Config."
    }

    $githubSourceKey = "source$githubSourceIndex"
    $escapedUser = [System.Security.SecurityElement]::Escape($resolvedGitHubPackagesUser)
    $escapedToken = [System.Security.SecurityElement]::Escape($resolvedGitHubPackagesToken)

    $githubPackagesCredentialsBlock = @"
  <packageSourceCredentials>
    <$githubSourceKey>
      <add key="Username" value="$escapedUser" />
      <add key="ClearTextPassword" value="$escapedToken" />
    </$githubSourceKey>
  </packageSourceCredentials>
"@
}

if (Test-Path -LiteralPath $smokeRoot)
{
    Remove-Item -LiteralPath $smokeRoot -Recurse -Force
}

Write-Host "Creating smoke consumer for $PackageId $version"
dotnet new console -n SmokeConsumer -o $smokeRoot --force
if ($LASTEXITCODE -ne 0)
{
    throw "dotnet new console failed."
}

$projectPath = Join-Path $smokeRoot "SmokeConsumer.csproj"

@"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
$($packageSourceEntries -join [Environment]::NewLine)
  </packageSources>
$githubPackagesCredentialsBlock
</configuration>
"@ | Set-Content -LiteralPath $nugetConfigPath -Encoding UTF8

Write-Host "Adding $PackageId from $resolvedPackageOutput"
dotnet add $projectPath package $PackageId --version $version --no-restore
if ($LASTEXITCODE -ne 0)
{
    throw "dotnet add package failed for $PackageId $version."
}

Write-Host "Restoring smoke consumer"
dotnet restore $projectPath --configfile $nugetConfigPath --packages $packageCacheRoot
if ($LASTEXITCODE -ne 0)
{
    throw "dotnet restore failed for the smoke consumer project."
}

Write-Host "Building smoke consumer"
dotnet build $projectPath -c Release --no-restore
if ($LASTEXITCODE -ne 0)
{
    throw "dotnet build failed for the smoke consumer project."
}
