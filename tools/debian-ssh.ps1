<#
.SYNOPSIS
Connects to a Debian VM over SSH (interactive shell or one-shot command).

.DESCRIPTION
Supports:
- Direct host targeting (-TargetHost) or Hyper-V VM name resolution (-VmName)
- Interactive shell passthrough (stdin/stdout/stderr)
- One-shot command execution through bash -lc
- Optional identity file and host key policy overrides

.EXAMPLE
powershell -ExecutionPolicy Bypass -File tools/debian-ssh.ps1 `
  -TargetHost debian.example.internal `
  -User debian

.EXAMPLE
powershell -ExecutionPolicy Bypass -File tools/debian-ssh.ps1 `
  -TargetHost debian.example.internal `
  -User debian `
  -Command "redis-cli -p 6379 ping"
#>
[CmdletBinding()]
param(
    [string]$TargetHost = "",
    [string]$VmName = "",
    [string]$User = "",
    [int]$Port = 22,
    [string]$IdentityFile = "",
    [string]$Command = "",
    [string]$ShellPath = "/bin/bash",
    [switch]$RawCommand,
    [switch]$DisableHostKeyChecking,
    [int]$ConnectTimeoutSeconds = 8
)

$ErrorActionPreference = "Stop"

function Convert-ToBashSingleQuotedLiteral {
    param([Parameter(Mandatory = $true)][string]$Text)

    $replacement = "'" + '"' + "'" + '"' + "'"
    return "'" + $Text.Replace("'", $replacement) + "'"
}

function Test-IPv4 {
    param([Parameter(Mandatory = $true)][string]$Value)
    return $Value -match '^(?:\d{1,3}\.){3}\d{1,3}$'
}

function Resolve-HostFromVmName {
    param([Parameter(Mandatory = $true)][string]$Name)

    if (-not (Get-Command Get-VMNetworkAdapter -ErrorAction SilentlyContinue)) {
        throw "Hyper-V cmdlets are unavailable. Provide -TargetHost explicitly."
    }

    $ipCandidates = @(
        Get-VMNetworkAdapter -VMName $Name -ErrorAction Stop |
            ForEach-Object { $_.IPAddresses } |
            Where-Object { $_ -and (Test-IPv4 $_) -and $_ -ne "127.0.0.1" } |
            Select-Object -Unique
    )

    if ($ipCandidates.Count -eq 0) {
        throw "No IPv4 address found for VM '$Name'."
    }

    $private = $ipCandidates | Where-Object {
        $_ -match '^10\.' -or
        $_ -match '^192\.168\.' -or
        $_ -match '^172\.(1[6-9]|2[0-9]|3[0-1])\.'
    }

    if ($private.Count -gt 0) {
        return $private[0]
    }

    return $ipCandidates[0]
}

if ([string]::IsNullOrWhiteSpace($TargetHost) -and -not [string]::IsNullOrWhiteSpace($env:VAPECACHE_DEBIAN_HOST)) {
    $TargetHost = $env:VAPECACHE_DEBIAN_HOST
}

if ([string]::IsNullOrWhiteSpace($User)) {
    if (-not [string]::IsNullOrWhiteSpace($env:VAPECACHE_DEBIAN_USER)) {
        $User = $env:VAPECACHE_DEBIAN_USER
    }
    else {
        $User = "debian"
    }
}

if ($Port -le 0) {
    throw "Port must be a positive integer."
}

if ([string]::IsNullOrWhiteSpace($TargetHost) -and [string]::IsNullOrWhiteSpace($VmName)) {
    throw "Provide either -TargetHost or -VmName (or set VAPECACHE_DEBIAN_HOST)."
}

if (-not [string]::IsNullOrWhiteSpace($TargetHost) -and -not [string]::IsNullOrWhiteSpace($VmName)) {
    Write-Warning "Both -TargetHost and -VmName were provided. Using -TargetHost."
}

if ([string]::IsNullOrWhiteSpace($TargetHost)) {
    $TargetHost = Resolve-HostFromVmName -Name $VmName
}

if ([string]::IsNullOrWhiteSpace($TargetHost)) {
    throw "Unable to resolve target host."
}

$sshExe = Get-Command ssh -ErrorAction SilentlyContinue
if ($null -eq $sshExe) {
    throw "OpenSSH client (ssh) is not available on this machine."
}

$sshArgs = @(
    "-p", "$Port",
    "-o", "ConnectTimeout=$ConnectTimeoutSeconds",
    "-o", "ServerAliveInterval=30",
    "-o", "ServerAliveCountMax=3"
)

if (-not [string]::IsNullOrWhiteSpace($Command)) {
    # Avoid hanging automation calls on password prompts.
    $sshArgs += @("-o", "BatchMode=yes")
}

if ($DisableHostKeyChecking) {
    $sshArgs += @("-o", "StrictHostKeyChecking=no", "-o", "UserKnownHostsFile=/dev/null")
}
else {
    $sshArgs += @("-o", "StrictHostKeyChecking=accept-new")
}

if (-not [string]::IsNullOrWhiteSpace($IdentityFile)) {
    $resolvedIdentityFile = Resolve-Path $IdentityFile -ErrorAction Stop
    $sshArgs += @("-i", "$resolvedIdentityFile")
}

$target = "$User@$TargetHost"

Write-Host "SSH target: $target"
Write-Host "Port: $Port"
Write-Host "Mode: $(if ([string]::IsNullOrWhiteSpace($Command)) { "interactive" } else { "command" })"

if ([string]::IsNullOrWhiteSpace($Command)) {
    $sshArgs += "-tt"
    & ssh @sshArgs $target
    $exitCode = $LASTEXITCODE
    exit $exitCode
}

$remoteCommand = if ($RawCommand) {
    $Command
}
else {
    $literal = Convert-ToBashSingleQuotedLiteral -Text $Command
    "$ShellPath -lc $literal"
}

& ssh @sshArgs $target $remoteCommand
$exitCode = $LASTEXITCODE
exit $exitCode
