param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")),
    [string]$OutputPath = "docs/SETTINGS_REFERENCE.md"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-CommentText {
    param([string]$Line)

    $trimmed = $Line.Trim()
    if (-not $trimmed.StartsWith("///")) {
        return ""
    }

    $body = $trimmed.Substring(3).Trim()
    $body = $body -replace "</?summary>", ""
    $body = $body -replace "</?remarks>", ""
    $body = $body -replace "</?value>", ""
    $body = $body -replace "<c>(.*?)</c>", '$1'
    $body = $body -replace '<see cref="(.*?)"\s*/>', '$1'
    return $body.Trim()
}

function New-OptionType {
    param(
        [string]$Name,
        [string]$Namespace,
        [string]$SourceFile,
        [string]$Summary,
        [string]$Kind
    )

    return [ordered]@{
        Name = $Name
        Namespace = $Namespace
        SourceFile = $SourceFile
        Summary = $Summary
        Kind = $Kind
        ConfigurationSection = $null
        Settings = New-Object System.Collections.Generic.List[object]
    }
}

function Add-Setting {
    param(
        [object]$TypeEntry,
        [string]$Name,
        [string]$Type,
        [string]$Default,
        [string]$Summary
    )

    $TypeEntry.Settings.Add([ordered]@{
        Name = $Name
        Type = $Type.Trim()
        Default = if ([string]::IsNullOrWhiteSpace($Default)) { "(none)" } else { $Default.Trim() }
        Summary = if ([string]::IsNullOrWhiteSpace($Summary)) { "(No XML summary.)" } else { $Summary.Trim() }
    })
}

function Get-RelativePathText {
    param(
        [string]$BasePath,
        [string]$TargetPath
    )

    $baseResolved = (Resolve-Path $BasePath).Path
    $targetResolved = (Resolve-Path $TargetPath).Path
    $baseUri = New-Object System.Uri(($baseResolved.TrimEnd('\') + '\'))
    $targetUri = New-Object System.Uri($targetResolved)
    $relative = $baseUri.MakeRelativeUri($targetUri).ToString()
    return [System.Uri]::UnescapeDataString($relative).Replace('\\', '/')
}

$optionsFiles = Get-ChildItem -Path $RepoRoot -Recurse -Filter "*Options.cs" -File |
    Where-Object {
        $_.FullName -notmatch "\\(bin|obj|TestResults|BenchmarkDotNet.Artifacts|\.tmp)\\"
    } |
    Sort-Object FullName

$types = New-Object System.Collections.Generic.List[object]

foreach ($file in $optionsFiles) {
    $relativePath = Get-RelativePathText -BasePath $RepoRoot -TargetPath $file.FullName
    $lines = Get-Content -Path $file.FullName

    $namespace = ""
    $pendingSummaryLines = New-Object System.Collections.Generic.List[string]
    $currentType = $null
    $braceDepth = 0

    for ($i = 0; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]
        $trimmed = $line.Trim()

        if ($trimmed.StartsWith("namespace ")) {
            $namespace = $trimmed.Substring("namespace ".Length).TrimEnd(';').Trim()
            continue
        }

        if ($trimmed.StartsWith("///")) {
            $text = Get-CommentText -Line $trimmed
            if (-not [string]::IsNullOrWhiteSpace($text)) {
                $pendingSummaryLines.Add($text)
            }
            continue
        }

        if ($trimmed.Length -eq 0) {
            continue
        }

        $typeMatch = [regex]::Match($trimmed, 'public\s+(?:sealed\s+|readonly\s+|partial\s+)*(record\s+struct|record|class)\s+([A-Za-z_][A-Za-z0-9_]*Options)\b')
        if ($typeMatch.Success) {
            $kind = $typeMatch.Groups[1].Value
            $name = $typeMatch.Groups[2].Value
            $summary = ($pendingSummaryLines -join " ").Trim()
            $pendingSummaryLines.Clear()

            $currentType = New-OptionType -Name $name -Namespace $namespace -SourceFile $relativePath -Summary $summary -Kind $kind
            $types.Add($currentType)

            if ($kind -eq "record struct" -and $trimmed.Contains("(") -and -not $trimmed.Contains("{")) {
                $signature = $trimmed
                $j = $i + 1
                while ($j -lt $lines.Length -and -not $signature.Contains(");")) {
                    $signature += " " + $lines[$j].Trim()
                    $j++
                }

                $inner = $signature.Substring($signature.IndexOf('(') + 1)
                $inner = $inner.Substring(0, $inner.LastIndexOf(')'))
                $parts = $inner.Split(',')
                foreach ($part in $parts) {
                    $p = $part.Trim()
                    if ([string]::IsNullOrWhiteSpace($p)) {
                        continue
                    }

                    $paramMatch = [regex]::Match($p, '([A-Za-z_][A-Za-z0-9_<>,\.\?\[\]]*)\s+([A-Za-z_][A-Za-z0-9_]*)(?:\s*=\s*(.+))?')
                    if ($paramMatch.Success) {
                        Add-Setting -TypeEntry $currentType -Name $paramMatch.Groups[2].Value -Type $paramMatch.Groups[1].Value -Default $paramMatch.Groups[3].Value -Summary "Primary constructor option."
                    }
                }

                $i = $j
                $currentType = $null
                $braceDepth = 0
                continue
            }

            $braceDepth = ([regex]::Matches($line, '\{')).Count - ([regex]::Matches($line, '\}')).Count
            continue
        }

        if ($null -ne $currentType) {
            if ($trimmed -match 'public\s+const\s+string\s+ConfigurationSectionName\s*=\s*"([^"]+)";') {
                $currentType.ConfigurationSection = $Matches[1]
                $pendingSummaryLines.Clear()
                continue
            }

            $propertyMatch = [regex]::Match($trimmed, 'public\s+([A-Za-z_][A-Za-z0-9_<>,\.\?\[\]\s]*)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\{\s*get;\s*(?:init;|set;)\s*\}\s*(?:=\s*([^;]+))?;')
            if ($propertyMatch.Success) {
                $propertySummary = ($pendingSummaryLines -join " ").Trim()
                Add-Setting -TypeEntry $currentType -Name $propertyMatch.Groups[2].Value -Type $propertyMatch.Groups[1].Value -Default $propertyMatch.Groups[3].Value -Summary $propertySummary
                $pendingSummaryLines.Clear()
                continue
            }

            $pendingSummaryLines.Clear()
            $braceDepth += ([regex]::Matches($line, '\{')).Count
            $braceDepth -= ([regex]::Matches($line, '\}')).Count
            if ($braceDepth -le 0) {
                $currentType = $null
                $braceDepth = 0
            }
        }
    }
}

$outputFullPath = Join-Path $RepoRoot $OutputPath
$linesOut = New-Object System.Collections.Generic.List[string]
$generatedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'")

$linesOut.Add("# Settings Reference")
$linesOut.Add("")
$linesOut.Add("Generated from source (*Options.cs) on $generatedAt.")
$linesOut.Add("")
$linesOut.Add("This reference is source-of-truth for every options setting and default currently implemented.")
$linesOut.Add("")

foreach ($type in ($types | Sort-Object Name)) {
    $linesOut.Add("## $($type.Name)")
    $linesOut.Add("")

    if (-not [string]::IsNullOrWhiteSpace($type.Summary)) {
        $linesOut.Add($type.Summary)
        $linesOut.Add("")
    }

    $linesOut.Add("- Namespace: $($type.Namespace)")
    $linesOut.Add("- Source: $($type.SourceFile)")
    if (-not [string]::IsNullOrWhiteSpace($type.ConfigurationSection)) {
        $linesOut.Add("- Configuration Section: $($type.ConfigurationSection)")
    }
    $linesOut.Add("")

    if ($type.Settings.Count -eq 0) {
        $linesOut.Add("No settable properties were detected.")
        $linesOut.Add("")
        continue
    }

    $linesOut.Add("| Setting | Type | Default | Description |")
    $linesOut.Add("|---|---|---|---|")
    foreach ($setting in $type.Settings) {
        $name = $setting.Name.Replace("|", "\\|")
        $typeText = $setting.Type.Replace("|", "\\|")
        $defaultText = $setting.Default.Replace("|", "\\|")
        $summaryText = $setting.Summary.Replace("|", "\\|")
        $linesOut.Add("| $name | $typeText | $defaultText | $summaryText |")
    }
    $linesOut.Add("")
}

$linesOut.Add("## Maintenance")
$linesOut.Add("")
$linesOut.Add("Regenerate after changing any *Options.cs file:")
$linesOut.Add("")
$linesOut.Add('```powershell')
$linesOut.Add('.\\tools\\Generate-SettingsReference.ps1')
$linesOut.Add('```')
$linesOut.Add("")

Set-Content -Path $outputFullPath -Value $linesOut -Encoding UTF8
Write-Host "Generated $OutputPath with $($types.Count) option types."
