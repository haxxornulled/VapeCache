<#
.SYNOPSIS
Bootstraps SSH key-based auth to a Debian host.

.DESCRIPTION
Generates an ed25519 keypair (if missing) and appends the public key to the
remote user's ~/.ssh/authorized_keys. This command may prompt for password.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File tools/debian-ssh-bootstrap.ps1 `
  -TargetHost debian.example.internal `
  -User debian
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$TargetHost,
    [string]$User = "debian",
    [int]$Port = 22,
    [string]$PrivateKeyPath = "$HOME\.ssh\id_ed25519"
)

$ErrorActionPreference = "Stop"

function Convert-ToBashSingleQuotedLiteral {
    param([Parameter(Mandatory = $true)][string]$Text)

    $replacement = "'" + '"' + "'" + '"' + "'"
    return "'" + $Text.Replace("'", $replacement) + "'"
}

$sshExe = Get-Command ssh -ErrorAction SilentlyContinue
$sshKeygenExe = Get-Command ssh-keygen -ErrorAction SilentlyContinue
if ($null -eq $sshExe -or $null -eq $sshKeygenExe) {
    throw "OpenSSH client tools (ssh/ssh-keygen) are required."
}

$resolvedKeyPath = [System.IO.Path]::GetFullPath($PrivateKeyPath)
$keyDir = Split-Path -Parent $resolvedKeyPath
if (-not (Test-Path $keyDir)) {
    New-Item -ItemType Directory -Path $keyDir -Force | Out-Null
}

if (-not (Test-Path $resolvedKeyPath)) {
    Write-Host "Generating SSH key: $resolvedKeyPath"
    & ssh-keygen -t ed25519 -f $resolvedKeyPath -N "" -C "$env:USERNAME@vapecache"
    if ($LASTEXITCODE -ne 0) {
        throw "ssh-keygen failed with exit code $LASTEXITCODE."
    }
}
else {
    Write-Host "Using existing key: $resolvedKeyPath"
}

$publicKeyPath = "$resolvedKeyPath.pub"
if (-not (Test-Path $publicKeyPath)) {
    throw "Public key not found: $publicKeyPath"
}

$publicKey = (Get-Content $publicKeyPath -Raw).Trim()
if ([string]::IsNullOrWhiteSpace($publicKey)) {
    throw "Public key file is empty: $publicKeyPath"
}

$target = "$User@$TargetHost"
Write-Host ("Installing public key on {0}:{1}" -f $target, $Port)

$pubLiteral = Convert-ToBashSingleQuotedLiteral -Text $publicKey
$remoteCmd = @(
    "umask 077",
    "mkdir -p ~/.ssh",
    "touch ~/.ssh/authorized_keys",
    "grep -qxF $pubLiteral ~/.ssh/authorized_keys || echo $pubLiteral >> ~/.ssh/authorized_keys",
    "chmod 700 ~/.ssh",
    "chmod 600 ~/.ssh/authorized_keys"
) -join "; "

& ssh "-p" "$Port" "-o" "StrictHostKeyChecking=accept-new" $target $remoteCmd
if ($LASTEXITCODE -ne 0) {
    throw "Failed to install SSH key on remote host."
}

Write-Host "Key install completed."
Write-Host "Testing non-interactive login..."
& ssh "-p" "$Port" "-o" "BatchMode=yes" $target "echo ssh-ok && whoami && hostname"
if ($LASTEXITCODE -ne 0) {
    throw "Key install succeeded but non-interactive login test failed."
}

Write-Host "SSH bootstrap complete."
