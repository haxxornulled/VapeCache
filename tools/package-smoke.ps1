param(
    [string]$PackageOutput = "artifacts/packages",
    [string]$PackageId = "VapeCache"
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
    <add key="local" value="$resolvedPackageOutput" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@ | Set-Content -LiteralPath $nugetConfigPath -Encoding UTF8

Write-Host "Adding $PackageId from $resolvedPackageOutput"
dotnet add $projectPath package $PackageId --version $version --package-directory $packageCacheRoot
if ($LASTEXITCODE -ne 0)
{
    throw "dotnet add package failed for $PackageId $version."
}

Write-Host "Building smoke consumer"
dotnet build $projectPath -c Release --no-restore
if ($LASTEXITCODE -ne 0)
{
    throw "dotnet build failed for the smoke consumer project."
}
