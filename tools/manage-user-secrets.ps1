[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet("SetKey", "RemoveKey", "SetBlock", "RemoveBlock", "RemoveSection")]
    [string]$Command,

    [string]$Project = "VapeCache.AppHost\VapeCache.AppHost.csproj",
    [string]$Key = "",
    [string]$Value = "",
    [string]$Section = "",
    [string]$JsonBlock = "",
    [string]$JsonFile = ""
)

$ErrorActionPreference = "Stop"

function Assert-RequiredValue
{
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Candidate
    )

    if ([string]::IsNullOrWhiteSpace($Candidate))
    {
        throw "$Name is required for command $Command."
    }
}

function Assert-CommandAvailable
{
    param([Parameter(Mandatory = $true)][string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue))
    {
        throw "Required command not found: $Name"
    }
}

function Get-RepoRoot
{
    return [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
}

function Resolve-AbsolutePath
{
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path))
    {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path (Get-RepoRoot) $Path))
}

function Invoke-UserSecretsCommand
{
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $output = & dotnet user-secrets @Arguments 2>&1
    if ($LASTEXITCODE -ne 0)
    {
        throw "dotnet user-secrets $($Arguments -join ' ') failed.`n$($output | Out-String)"
    }

    return $output
}

function Get-JsonInputText
{
    if (-not [string]::IsNullOrWhiteSpace($JsonBlock) -and -not [string]::IsNullOrWhiteSpace($JsonFile))
    {
        throw "Specify either -JsonBlock or -JsonFile, not both."
    }

    if (-not [string]::IsNullOrWhiteSpace($JsonFile))
    {
        $resolvedJsonFile = Resolve-AbsolutePath -Path $JsonFile
        return Get-Content $resolvedJsonFile -Raw
    }

    if (-not [string]::IsNullOrWhiteSpace($JsonBlock))
    {
        return $JsonBlock
    }

    throw "A JSON payload is required. Pass -JsonBlock or -JsonFile."
}

function Convert-ToSecretScalarText
{
    param($InputValue)

    if ($null -eq $InputValue)
    {
        return $null
    }

    if ($InputValue -is [string])
    {
        return $InputValue
    }

    if ($InputValue -is [bool])
    {
        return $InputValue.ToString().ToLowerInvariant()
    }

    if ($InputValue -is [System.DateTime])
    {
        return $InputValue.ToString("O", [System.Globalization.CultureInfo]::InvariantCulture)
    }

    if ($InputValue -is [System.IFormattable])
    {
        return $InputValue.ToString($null, [System.Globalization.CultureInfo]::InvariantCulture)
    }

    return [string]$InputValue
}

function Add-SecretEntries
{
    param(
        [Parameter(Mandatory = $true)][hashtable]$Target,
        [Parameter(Mandatory = $true)][hashtable]$Entries
    )

    foreach ($entryKey in $Entries.Keys)
    {
        $Target[$entryKey] = $Entries[$entryKey]
    }
}

function Expand-SecretEntries
{
    param(
        $InputValue,
        [string]$Prefix = "",
        [switch]$IncludeNullLeaves
    )

    $entries = @{}

    if ($null -eq $InputValue)
    {
        if ($IncludeNullLeaves.IsPresent -and -not [string]::IsNullOrWhiteSpace($Prefix))
        {
            $entries[$Prefix] = $null
        }

        return $entries
    }

    if ($InputValue -is [System.Collections.IDictionary])
    {
        foreach ($childKey in $InputValue.Keys)
        {
            $childPrefix = if ([string]::IsNullOrWhiteSpace($Prefix)) { [string]$childKey } else { "${Prefix}:$childKey" }
            Add-SecretEntries -Target $entries -Entries (Expand-SecretEntries -InputValue $InputValue[$childKey] -Prefix $childPrefix -IncludeNullLeaves:$IncludeNullLeaves)
        }

        return $entries
    }

    if ($InputValue -is [System.Management.Automation.PSCustomObject])
    {
        foreach ($property in $InputValue.PSObject.Properties)
        {
            $childPrefix = if ([string]::IsNullOrWhiteSpace($Prefix)) { $property.Name } else { "${Prefix}:$($property.Name)" }
            Add-SecretEntries -Target $entries -Entries (Expand-SecretEntries -InputValue $property.Value -Prefix $childPrefix -IncludeNullLeaves:$IncludeNullLeaves)
        }

        return $entries
    }

    if ($InputValue -is [System.Collections.IEnumerable] -and -not ($InputValue -is [string]))
    {
        $index = 0
        foreach ($item in $InputValue)
        {
            $childPrefix = if ([string]::IsNullOrWhiteSpace($Prefix)) { "$index" } else { "${Prefix}:$index" }
            Add-SecretEntries -Target $entries -Entries (Expand-SecretEntries -InputValue $item -Prefix $childPrefix -IncludeNullLeaves:$IncludeNullLeaves)
            $index++
        }

        return $entries
    }

    if ([string]::IsNullOrWhiteSpace($Prefix))
    {
        throw "The JSON block must resolve to an object or section-backed object, not a scalar root value."
    }

    $entries[$Prefix] = Convert-ToSecretScalarText -InputValue $InputValue
    return $entries
}

function Try-GetJsonChild
{
    param(
        $InputValue,
        [Parameter(Mandatory = $true)][string]$Segment,
        [ref]$Child
    )

    if ($InputValue -is [System.Collections.IDictionary])
    {
        if ($InputValue.Contains($Segment))
        {
            $Child.Value = $InputValue[$Segment]
            return $true
        }

        return $false
    }

    if ($InputValue -is [System.Management.Automation.PSCustomObject])
    {
        $property = $InputValue.PSObject.Properties[$Segment]
        if ($null -ne $property)
        {
            $Child.Value = $property.Value
            return $true
        }

        return $false
    }

    if ($InputValue -is [System.Collections.IList])
    {
        $index = 0
        if ([int]::TryParse($Segment, [ref]$index) -and $index -ge 0 -and $index -lt $InputValue.Count)
        {
            $Child.Value = $InputValue[$index]
            return $true
        }
    }

    return $false
}

function Resolve-JsonSectionTarget
{
    param(
        $Document,
        [string]$SectionPath
    )

    if ([string]::IsNullOrWhiteSpace($SectionPath))
    {
        return @{
            Prefix = ""
            Value = $Document
        }
    }

    $current = $Document
    $found = $true
    foreach ($segment in $SectionPath.Split(':', [System.StringSplitOptions]::RemoveEmptyEntries))
    {
        $child = $null
        if (-not (Try-GetJsonChild -InputValue $current -Segment $segment -Child ([ref]$child)))
        {
            $found = $false
            break
        }

        $current = $child
    }

    if ($found)
    {
        return @{
            Prefix = $SectionPath
            Value = $current
        }
    }

    return @{
        Prefix = $SectionPath
        Value = $Document
    }
}

function Get-SecretEntriesFromJson
{
    param(
        [Parameter(Mandatory = $true)][string]$JsonText,
        [string]$SectionPath = "",
        [switch]$IncludeNullLeaves
    )

    $document = ConvertFrom-Json -InputObject $JsonText
    $resolved = Resolve-JsonSectionTarget -Document $document -SectionPath $SectionPath
    return Expand-SecretEntries -InputValue $resolved.Value -Prefix $resolved.Prefix -IncludeNullLeaves:$IncludeNullLeaves
}

function Get-UserSecretsList
{
    param([Parameter(Mandatory = $true)][string]$ProjectPath)

    $lines = Invoke-UserSecretsCommand -Arguments @("list", "--project", $ProjectPath)
    $entries = @{}

    foreach ($line in $lines)
    {
        $text = [string]$line
        if ([string]::IsNullOrWhiteSpace($text))
        {
            continue
        }

        $separatorIndex = $text.IndexOf(" = ", [System.StringComparison]::Ordinal)
        if ($separatorIndex -lt 0)
        {
            continue
        }

        $entryKey = $text.Substring(0, $separatorIndex).Trim()
        $entryValue = $text.Substring($separatorIndex + 3)
        $entries[$entryKey] = $entryValue
    }

    return $entries
}

function Get-MatchingSecretKeys
{
    param(
        [Parameter(Mandatory = $true)][hashtable]$Entries,
        [Parameter(Mandatory = $true)][string]$SectionPath
    )

    $matches = New-Object System.Collections.Generic.List[string]
    foreach ($entryKey in $Entries.Keys)
    {
        if ($entryKey.Equals($SectionPath, [System.StringComparison]::OrdinalIgnoreCase) -or
            $entryKey.StartsWith("${SectionPath}:", [System.StringComparison]::OrdinalIgnoreCase))
        {
            $matches.Add($entryKey)
        }
    }

    return $matches
}

function Set-UserSecretKey
{
    param(
        [Parameter(Mandatory = $true)][string]$ProjectPath,
        [Parameter(Mandatory = $true)][string]$SecretKey,
        [Parameter(Mandatory = $true)][string]$SecretValue
    )

    if ($PSCmdlet.ShouldProcess($SecretKey, "Set user secret"))
    {
        Invoke-UserSecretsCommand -Arguments @("set", $SecretKey, $SecretValue, "--project", $ProjectPath) | Out-Null
        Write-Host "Set user secret: $SecretKey"
    }
}

function Remove-UserSecretKey
{
    param(
        [Parameter(Mandatory = $true)][string]$ProjectPath,
        [Parameter(Mandatory = $true)][string]$SecretKey
    )

    if ($PSCmdlet.ShouldProcess($SecretKey, "Remove user secret"))
    {
        Invoke-UserSecretsCommand -Arguments @("remove", $SecretKey, "--project", $ProjectPath) | Out-Null
        Write-Host "Removed user secret: $SecretKey"
    }
}

function Set-UserSecretsBlock
{
    param(
        [Parameter(Mandatory = $true)][string]$ProjectPath,
        [Parameter(Mandatory = $true)][string]$JsonText,
        [string]$SectionPath = ""
    )

    $entries = Get-SecretEntriesFromJson -JsonText $JsonText -SectionPath $SectionPath
    if ($entries.Count -eq 0)
    {
        Write-Host "No leaf values were found in the JSON block. Nothing to set."
        return
    }

    foreach ($entryKey in ($entries.Keys | Sort-Object))
    {
        $entryValue = $entries[$entryKey]
        if ($null -eq $entryValue)
        {
            Write-Host "Skipping null leaf: $entryKey"
            continue
        }

        Set-UserSecretKey -ProjectPath $ProjectPath -SecretKey $entryKey -SecretValue $entryValue
    }
}

function Remove-UserSecretsBlock
{
    param(
        [Parameter(Mandatory = $true)][string]$ProjectPath,
        [Parameter(Mandatory = $true)][string]$JsonText,
        [string]$SectionPath = ""
    )

    $entries = Get-SecretEntriesFromJson -JsonText $JsonText -SectionPath $SectionPath -IncludeNullLeaves
    if ($entries.Count -eq 0)
    {
        Write-Host "No leaf values were found in the JSON block. Nothing to remove."
        return
    }

    foreach ($entryKey in ($entries.Keys | Sort-Object))
    {
        Remove-UserSecretKey -ProjectPath $ProjectPath -SecretKey $entryKey
    }
}

function Remove-UserSecretsSection
{
    param(
        [Parameter(Mandatory = $true)][string]$ProjectPath,
        [Parameter(Mandatory = $true)][string]$SectionPath
    )

    $existingEntries = Get-UserSecretsList -ProjectPath $ProjectPath
    $matches = Get-MatchingSecretKeys -Entries $existingEntries -SectionPath $SectionPath
    if ($matches.Count -eq 0)
    {
        Write-Host "No user secrets found under section: $SectionPath"
        return
    }

    foreach ($entryKey in ($matches | Sort-Object))
    {
        Remove-UserSecretKey -ProjectPath $ProjectPath -SecretKey $entryKey
    }
}

Assert-CommandAvailable -Name "dotnet"

$resolvedProject = Resolve-AbsolutePath -Path $Project

switch ($Command)
{
    "SetKey"
    {
        Assert-RequiredValue -Name "Key" -Candidate $Key
        Assert-RequiredValue -Name "Value" -Candidate $Value
        Set-UserSecretKey -ProjectPath $resolvedProject -SecretKey $Key -SecretValue $Value
        break
    }
    "RemoveKey"
    {
        Assert-RequiredValue -Name "Key" -Candidate $Key
        Remove-UserSecretKey -ProjectPath $resolvedProject -SecretKey $Key
        break
    }
    "SetBlock"
    {
        $jsonText = Get-JsonInputText
        Set-UserSecretsBlock -ProjectPath $resolvedProject -JsonText $jsonText -SectionPath $Section
        break
    }
    "RemoveBlock"
    {
        $jsonText = Get-JsonInputText
        Remove-UserSecretsBlock -ProjectPath $resolvedProject -JsonText $jsonText -SectionPath $Section
        break
    }
    "RemoveSection"
    {
        Assert-RequiredValue -Name "Section" -Candidate $Section
        Remove-UserSecretsSection -ProjectPath $resolvedProject -SectionPath $Section
        break
    }
    default
    {
        throw "Unsupported command: $Command"
    }
}
