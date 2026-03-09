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
        ForEach-Object {
            $absoluteFile = [System.IO.Path]::GetFullPath($_.File)
            $relativeFile = [System.IO.Path]::GetRelativePath($repoRoot, $absoluteFile).Replace('\', '/')
            [pscustomobject]@{
                file = $relativeFile
                line = [int]$_.Line
                rule = $_.Rule
            }
        } |
        Sort-Object file, line, rule

    return [pscustomobject]@{
        formatVersion = 2
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
$baselineFormatVersion = if ($null -eq $baseline.formatVersion) { 1 } else { [int]$baseline.formatVersion }
$baselineIsLineLevel = $baselineFormatVersion -ge 2

$violations = New-Object System.Collections.Generic.List[object]
if ($baselineIsLineLevel) {
    $baselineSet = @{}
    foreach ($entry in $baseline.entries) {
        $key = "$($entry.file)|$([int]$entry.line)|$($entry.rule)"
        $baselineSet[$key] = $true
    }

    foreach ($entry in $snapshot.entries) {
        $key = "$($entry.file)|$([int]$entry.line)|$($entry.rule)"
        if (-not $baselineSet.ContainsKey($key)) {
            $violations.Add([pscustomobject]@{
                file = $entry.file
                line = [int]$entry.line
                rule = $entry.rule
            }) | Out-Null
        }
    }
}
else {
    # Backward compatibility for v1 baseline format (file+rule count).
    $baselineMap = @{}
    foreach ($entry in $baseline.entries) {
        $key = "$($entry.file)|$($entry.rule)"
        $baselineMap[$key] = [int]$entry.count
    }

    $currentMap = @{}
    foreach ($entry in $snapshot.entries) {
        $key = "$($entry.file)|$($entry.rule)"
        if ($currentMap.ContainsKey($key)) {
            $currentMap[$key] = [int]$currentMap[$key] + 1
        }
        else {
            $currentMap[$key] = 1
        }
    }

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
}

Write-Host "Runtime analyzer baseline check:"
Write-Host "  Baseline unique warning locations: $($baseline.totals.uniqueWarningLocations)"
Write-Host "  Current  unique warning locations: $($snapshot.totals.uniqueWarningLocations)"

if ($violations.Count -gt 0) {
    Write-Host ""
    Write-Host "Runtime analyzer warning baseline regression detected:" -ForegroundColor Red
    if ($baselineIsLineLevel) {
        $violations |
            Sort-Object file, line, rule |
            Select-Object -First 25 |
            ForEach-Object {
                Write-Host ("  +1 {0} {1}:{2}" -f $_.rule, $_.file, $_.line) -ForegroundColor Red
            }
    }
    else {
        $violations |
            Sort-Object -Property @{Expression = "delta"; Descending = $true }, file, rule |
            Select-Object -First 25 |
            ForEach-Object {
                Write-Host ("  +{0} {1} {2} (baseline={3}, current={4})" -f $_.delta, $_.rule, $_.file, $_.baseline, $_.current) -ForegroundColor Red
            }
    }

    throw "Runtime analyzer warning counts increased. Burn down warnings or update baseline intentionally."
}

Write-Host "Runtime analyzer baseline check passed."
