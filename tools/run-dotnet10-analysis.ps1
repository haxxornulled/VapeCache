param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$TreatWarningsAsErrors,
    [switch]$RunTests = $true,
    [switch]$VerifyFormatting
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

Write-Host "Running .NET 10 analyzer build for VapeCache..."

$buildArgs = @(
    "build",
    "VapeCache.sln",
    "-c", $Configuration,
    "/p:EnableNETAnalyzers=true",
    "/p:AnalysisLevel=latest-recommended",
    "/p:EnforceCodeStyleInBuild=true"
)

if ($TreatWarningsAsErrors) {
    $buildArgs += "/warnaserror"
}

& dotnet @buildArgs

if ($VerifyFormatting) {
    Write-Host "Verifying analyzer/code-style formatting..."
    & dotnet format analyzers "VapeCache.sln" --verify-no-changes --severity warn
}

if ($RunTests) {
    Write-Host "Running test suite..."
    & dotnet test "VapeCache.Tests\VapeCache.Tests.csproj" -c $Configuration --no-build
}

Write-Host "Analyzer pipeline completed successfully."
