# Debian SSH Tooling

This repo includes a helper script for connecting to a Debian VM over SSH:

- Script: `tools/debian-ssh.ps1`

## Quick Start

Interactive shell:

```powershell
powershell -ExecutionPolicy Bypass -File tools/debian-ssh.ps1 `
  -TargetHost debian.example.internal `
  -User debian
```

Run one command through `bash -lc`:

```powershell
powershell -ExecutionPolicy Bypass -File tools/debian-ssh.ps1 `
  -TargetHost debian.example.internal `
  -User debian `
  -Command "hostname && whoami"
```

Redis check on remote host:

```powershell
powershell -ExecutionPolicy Bypass -File tools/debian-ssh.ps1 `
  -TargetHost debian.example.internal `
  -User debian `
  -Command "redis-cli -h 127.0.0.1 -p 6379 ping"
```

## Hyper-V VM Name Mode

If Hyper-V PowerShell cmdlets are available and permitted, host IP can be resolved from VM name:

```powershell
powershell -ExecutionPolicy Bypass -File tools/debian-ssh.ps1 `
  -VmName Debian `
  -User debian `
  -Command "hostname -I"
```

## Bootstrap Key Auth (Recommended)

If command mode returns `Permission denied (publickey,password)`, install your
local public key to the remote host:

```powershell
powershell -ExecutionPolicy Bypass -File tools/debian-ssh-bootstrap.ps1 `
  -TargetHost debian.example.internal `
  -User debian
```

Then retry:

```powershell
powershell -ExecutionPolicy Bypass -File tools/debian-ssh.ps1 `
  -TargetHost debian.example.internal `
  -User debian `
  -Command "hostname && whoami"
```

## Environment Defaults

You can set defaults once:

```powershell
$env:VAPECACHE_DEBIAN_HOST = "debian.example.internal"
$env:VAPECACHE_DEBIAN_USER = "debian"
```

Then run:

```powershell
powershell -ExecutionPolicy Bypass -File tools/debian-ssh.ps1 -Command "hostname"
```
