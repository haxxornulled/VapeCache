param(
    [string]$PackageOutput = "artifacts/packages",
    [string]$PackageId = "VapeCache.Runtime",
    [string[]]$AdditionalPackageSources = @()
)

$ErrorActionPreference = "Stop"

$resolvedPackageOutput = (Resolve-Path -LiteralPath $PackageOutput).Path

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
$packageSourceEntries = for ($index = 0; $index -lt $packageSources.Count; $index++)
{
    $source = $packageSources[$index]
    "    <add key=""source$index"" value=""$source"" />"
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
