param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$Solution = "",
    [string]$BaselinePath = "tools/analyzer-baselines/runtime-warning-baseline.json",
    [switch]$UpdateBaseline
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

function Resolve-SolutionPath {
    param(
        [AllowEmptyString()]
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

function Get-RuntimeWarningSnapshot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SolutionPath,
        [Parameter(Mandatory = $true)]
        [string]$BuildConfiguration
    )

    $buildOutput = & dotnet build $SolutionPath -c $BuildConfiguration -t:Rebuild --nologo 2>&1
    if ($LASTEXITCODE -ne 0) {
        $buildOutput | ForEach-Object { Write-Host $_ }
        throw "Analyzer baseline build failed (dotnet exit code: $LASTEXITCODE)."
    }

    $warningRegex = '^(.*\.cs)\((\d+),(\d+)\): warning (CA\d+):'
    $allMatches = @(
        $buildOutput |
            Select-String -Pattern $warningRegex |
            ForEach-Object {
                $m = $_.Matches[0]
                [pscustomobject]@{
                    File = $m.Groups[1].Value
                    Line = [int]$m.Groups[2].Value
                    Rule = $m.Groups[4].Value
                }
            }
    )

    $runtimeMatches = $allMatches | Where-Object {
        $_.File -notmatch '\\VapeCache\.Tests\\|\\VapeCache\.PerfGates\.Tests\\|\\VapeCache\.Benchmarks\\|\\VapeCache\.Console\\|\\samples\\|\\VapeCache\.DocCommenter\\'
    }

    $uniqueLocations = $runtimeMatches |
        Sort-Object File, Line, Rule -Unique

    $entries = $uniqueLocations |
        Group-Object -Property File, Rule |
        ForEach-Object {
            $first = $_.Group[0]
            $absoluteFile = [System.IO.Path]::GetFullPath($first.File)
            $relativeFile = [System.IO.Path]::GetRelativePath($repoRoot, $absoluteFile).Replace('\', '/')
            [pscustomobject]@{
                file = $relativeFile
                rule = $first.Rule
                count = $_.Count
            }
        } |
        Sort-Object file, rule

    return [pscustomobject]@{
        formatVersion = 1
        generatedUtc = [DateTimeOffset]::UtcNow.ToString("O")
        configuration = $BuildConfiguration
        totals = [pscustomobject]@{
            uniqueWarningLocations = $uniqueLocations.Count
            entryCount = @($entries).Count
        }
        entries = @($entries)
    }
}

$resolvedSolution = Resolve-SolutionPath -RequestedPath $Solution
$snapshot = Get-RuntimeWarningSnapshot -SolutionPath $resolvedSolution -BuildConfiguration $Configuration
$baselineFullPath = Join-Path $repoRoot $BaselinePath

if ($UpdateBaseline) {
    $baselineDir = Split-Path $baselineFullPath -Parent
    if (-not (Test-Path $baselineDir)) {
        New-Item -ItemType Directory -Path $baselineDir -Force | Out-Null
    }

    $snapshot | ConvertTo-Json -Depth 6 | Set-Content -Path $baselineFullPath -Encoding utf8
    Write-Host "Runtime analyzer baseline updated: $baselineFullPath"
    Write-Host "Unique warning locations: $($snapshot.totals.uniqueWarningLocations)"
    exit 0
}

if (-not (Test-Path $baselineFullPath)) {
    throw "Baseline file not found: $baselineFullPath. Run with -UpdateBaseline to create it."
}

$baseline = Get-Content $baselineFullPath -Raw | ConvertFrom-Json
$baselineMap = @{}
foreach ($entry in $baseline.entries) {
    $key = "$($entry.file)|$($entry.rule)"
    $baselineMap[$key] = [int]$entry.count
}

$currentMap = @{}
foreach ($entry in $snapshot.entries) {
    $key = "$($entry.file)|$($entry.rule)"
    $currentMap[$key] = [int]$entry.count
}

$violations = New-Object System.Collections.Generic.List[object]
foreach ($kv in $currentMap.GetEnumerator()) {
    $baselineCount = if ($baselineMap.ContainsKey($kv.Key)) { $baselineMap[$kv.Key] } else { 0 }
    if ($kv.Value -gt $baselineCount) {
        $parts = $kv.Key.Split('|')
        $violations.Add([pscustomobject]@{
            file = $parts[0]
            rule = $parts[1]
            baseline = $baselineCount
            current = $kv.Value
            delta = $kv.Value - $baselineCount
        }) | Out-Null
    }
}

Write-Host "Runtime analyzer baseline check:"
Write-Host "  Baseline unique warning locations: $($baseline.totals.uniqueWarningLocations)"
Write-Host "  Current  unique warning locations: $($snapshot.totals.uniqueWarningLocations)"

if ($violations.Count -gt 0) {
    Write-Host ""
    Write-Host "Runtime analyzer warning baseline regression detected:" -ForegroundColor Red
    $violations |
        Sort-Object -Property @{Expression = "delta"; Descending = $true }, file, rule |
        Select-Object -First 25 |
        ForEach-Object {
            Write-Host ("  +{0} {1} {2} (baseline={3}, current={4})" -f $_.delta, $_.rule, $_.file, $_.baseline, $_.current) -ForegroundColor Red
        }

    throw "Runtime analyzer warning counts increased. Burn down warnings or update baseline intentionally."
}

Write-Host "Runtime analyzer baseline check passed."
