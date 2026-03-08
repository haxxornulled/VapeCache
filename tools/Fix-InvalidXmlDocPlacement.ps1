param(
    [string]$Root = (Resolve-Path ".")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$files = Get-ChildItem -Path $Root -Recurse -Filter "*.cs" -File |
    Where-Object { $_.FullName -notmatch "\\(bin|obj|TestResults|BenchmarkDotNet.Artifacts|\.tmp)\\" }

$removed = 0
$changedFiles = 0

foreach ($file in $files) {
    $lines = [System.Collections.Generic.List[string]]::new()
    foreach ($l in (Get-Content -Path $file.FullName)) { $lines.Add([string]$l) }

    $changed = $false
    $i = 0
    while ($i -le $lines.Count - 4) {
        $a = $lines[$i].Trim()
        $b = $lines[$i + 1].Trim()
        $c = $lines[$i + 2].Trim()
        $next = $lines[$i + 3].Trim()

        if ($a -eq "/// <summary>" -and $c -eq "/// </summary>" -and $b.StartsWith("///")) {
            $paramLike = $next -match '^[A-Za-z_][A-Za-z0-9_<>,\.\?\[\]]*\s+[A-Za-z_][A-Za-z0-9_]*\s*(=|,|\))'
            $declLike = $next -match '^(public|internal|protected|private|sealed|readonly|class|record|struct|interface|enum)\b'
            if ($paramLike -and -not $declLike) {
                $lines.RemoveAt($i)
                $lines.RemoveAt($i)
                $lines.RemoveAt($i)
                $removed += 3
                $changed = $true
                continue
            }
        }

        $i++
    }

    if ($changed) {
        Set-Content -Path $file.FullName -Value $lines -Encoding UTF8
        $changedFiles++
    }
}

Write-Host "Removed $removed invalid XML doc lines across $changedFiles files."
