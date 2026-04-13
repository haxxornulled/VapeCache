param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$Filter = "",
    [string]$Artifacts = "",
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ExtraArgs
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repoRoot "VapeCache.Benchmarks\VapeCache.Benchmarks.csproj"

$arguments = @(
    "run",
    "-c", $Configuration,
    "--project", $projectPath,
    "--"
)

if (-not [string]::IsNullOrWhiteSpace($Filter)) {
    $arguments += @("--filter", $Filter)
}

if (-not [string]::IsNullOrWhiteSpace($Artifacts)) {
    $arguments += @("--artifacts", $Artifacts)
}

if ($null -ne $ExtraArgs -and $ExtraArgs.Count -gt 0) {
    $arguments += $ExtraArgs
}

& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Benchmark run failed with exit code $LASTEXITCODE."
}