param(
    [string]$LogPath = ".tmp/cs1591_full.log"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path $LogPath)) {
    throw "Log file not found: $LogPath"
}

function Get-Indentation {
    param([string]$Line)
    $match = [regex]::Match($Line, '^\s*')
    return $match.Value
}

function To-Words {
    param([string]$Name)

    if ([string]::IsNullOrWhiteSpace($Name)) {
        return "member"
    }

    $clean = $Name.Trim('`')
    $clean = $clean -replace '<.*?>', ''
    $clean = $clean -replace '^I(?=[A-Z])', ''
    $words = [regex]::Replace($clean, '([a-z0-9])([A-Z])', '$1 $2')
    return $words.ToLowerInvariant()
}

function Get-SummaryText {
    param([string]$Line)

    $trimmed = $Line.Trim()

    $typeMatch = [regex]::Match($trimmed, '\binterface\s+([A-Za-z_][A-Za-z0-9_]*)')
    if ($typeMatch.Success) {
        $words = To-Words $typeMatch.Groups[1].Value
        return "Defines the $words contract."
    }

    $typeMatch = [regex]::Match($trimmed, '\b(class|record|struct|enum)\s+([A-Za-z_][A-Za-z0-9_]*)')
    if ($typeMatch.Success) {
        $words = To-Words $typeMatch.Groups[2].Value
        return "Represents the $words."
    }

    if ($trimmed -match '^[A-Za-z_][A-Za-z0-9_]*\s*(=|,|$)') {
        $name = ($trimmed -replace '\s*(=|,).*$', '').Trim()
        $words = To-Words $name
        return "Specifies $words."
    }

    $methodMatch = [regex]::Match($trimmed, '([A-Za-z_][A-Za-z0-9_]*)\s*\(')
    if ($methodMatch.Success) {
        $words = To-Words $methodMatch.Groups[1].Value
        return "Executes $words."
    }

    $propertyMatch = [regex]::Match($trimmed, '([A-Za-z_][A-Za-z0-9_]*)\s*\{\s*get;')
    if ($propertyMatch.Success) {
        $name = $propertyMatch.Groups[1].Value
        $words = To-Words $name
        if ($trimmed -match '\b(set;|init;)') {
            return "Gets or sets the $words."
        }

        return "Gets the $words."
    }

    $fieldMatch = [regex]::Match($trimmed, '(?:public|protected|internal)\s+(?:const\s+|static\s+|readonly\s+)*[A-Za-z_][A-Za-z0-9_<>,\.\?\[\]]*\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?:=|;)')
    if ($fieldMatch.Success) {
        $words = To-Words $fieldMatch.Groups[1].Value
        return "Defines the $words."
    }

    return "Provides member behavior."
}

function Has-XmlDocAbove {
    param(
        [System.Collections.Generic.List[string]]$Lines,
        [int]$Index
    )

    for ($i = $Index - 1; $i -ge 0; $i--) {
        $trim = $Lines[$i].Trim()
        if ($trim.Length -eq 0) {
            continue
        }

        return $trim.StartsWith("///")
    }

    return $false
}

$pattern = '^(.*\.cs)\((\d+),(\d+)\): error CS1591:'
$matches = Select-String -Path $LogPath -Pattern $pattern

if ($matches.Count -eq 0) {
    Write-Host "No CS1591 errors found in $LogPath"
    exit 0
}

$targets = @{}
foreach ($match in $matches) {
    $file = $match.Matches[0].Groups[1].Value
    $line = [int]$match.Matches[0].Groups[2].Value

    if (-not $targets.ContainsKey($file)) {
        $targets[$file] = New-Object System.Collections.Generic.HashSet[int]
    }

    [void]$targets[$file].Add($line)
}

$totalInserted = 0

foreach ($file in $targets.Keys) {
    if (-not (Test-Path $file)) {
        continue
    }

    $content = Get-Content -Path $file
    $lines = New-Object 'System.Collections.Generic.List[string]'
    foreach ($line in $content) {
        [void]$lines.Add([string]$line)
    }

    $lineNumbers = $targets[$file] | Sort-Object -Descending

    foreach ($lineNumber in $lineNumbers) {
        $index = $lineNumber - 1
        if ($index -lt 0 -or $index -ge $lines.Count) {
            continue
        }

        $insertionIndex = $index
        while ($insertionIndex -gt 0) {
            $prev = $lines[$insertionIndex - 1].Trim()
            if ($prev.StartsWith("[")) {
                $insertionIndex--
                continue
            }
            break
        }

        if (Has-XmlDocAbove -Lines $lines -Index $insertionIndex) {
            continue
        }

        $declarationLine = $lines[$index]
        $trimmed = $declarationLine.Trim()
        if ($trimmed.StartsWith("///") -or $trimmed.StartsWith("//")) {
            continue
        }

        $summary = Get-SummaryText -Line $declarationLine
        $indent = Get-Indentation -Line $declarationLine

        $lines.Insert($insertionIndex, "$indent/// <summary>")
        $lines.Insert($insertionIndex + 1, "$indent/// $summary")
        $lines.Insert($insertionIndex + 2, "$indent/// </summary>")
        $totalInserted += 3
    }

    Set-Content -Path $file -Value $lines -Encoding UTF8
}

Write-Host "Inserted $totalInserted XML doc lines from CS1591 log."
