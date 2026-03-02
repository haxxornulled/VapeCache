# VapeCache Repository Setup Guide

## 🚨 CRITICAL SECURITY ISSUE DETECTED

**A legacy HMAC signing secret had been committed in local git history but NOT pushed to GitHub.**

Commits containing the secret:
- `a74de57` - Major: Simplify licensing to application-based model
- `4ab4d34` - Move reconciliation to Enterprise-only tier
- `58cd1f2` - Add commercial licensing with Open Core model

**ACTION REQUIRED**: Do NOT push these commits to a public repository.

---

## Repository Strategy: Option B (Recommended)

### Create Two Separate Repositories

#### 1. **Public Repository** (New, Clean History)
**Repository**: `github.com/haxxornulled/VapeCache` (make public)

**Included Projects**:
- ✅ VapeCache (main package)
- ✅ VapeCache.Abstractions
- ✅ VapeCache.Infrastructure
- ✅ VapeCache.Extensions.Aspire
- ✅ VapeCache.Benchmarks
- ✅ VapeCache.Tests (OSS tests only)
- ✅ VapeCache.Console (demo/examples)
- ✅ Documentation (`/docs`, `/samples`)
- ✅ README.md
- ✅ LICENSE (MIT)

**Excluded**:
- ❌ VapeCache.Licensing
- ❌ VapeCache.LicenseGenerator
- ❌ VapeCache.Persistence
- ❌ VapeCache.Reconciliation
- ❌ VapeCache.Application
- ❌ ENTERPRISE_STRATEGY.md

#### 2. **Private Repository** (Current Repo)
**Repository**: `github.com/haxxornulled/VapeCache-Enterprise` (keep private)

**Contains**:
- ✅ OSS runtime plus enterprise cache packages
- ✅ Enterprise projects (Persistence, Reconciliation)
- ✅ Business and release tooling for the enterprise distribution
- ✅ ENTERPRISE_STRATEGY.md
- ✅ Customer data, licenses, business docs

**Licensing Domain**:
- ✅ `github.com/haxxornulled/VapeCache.Licensing` (keep private)
- ✅ `VapeCache.Licensing`, `VapeCache.LicenseGenerator`, and `VapeCache.Licensing.ControlPlane`

---

## Step-by-Step Setup

### Step 1: Rename Current Repo to Enterprise

```bash
# Navigate to repo
cd "c:\Visual Studio Projects\VapeCache"

# Update remote URL to enterprise repo
git remote set-url origin https://github.com/haxxornulled/VapeCache-Enterprise.git

# Verify
git remote -v
```

### Step 2: Create Fresh Public Repository

```bash
# Create new directory for public repo
cd "c:\Visual Studio Projects"
mkdir VapeCache-Public
cd VapeCache-Public

# Initialize new git repo
git init
git checkout -b main

# Copy open source projects only
# (See script below)
```

### Step 3: Use Automated Copy Script

Create `copy-to-public.ps1`:

```powershell
# VapeCache Open Source Repository Copy Script
# This script copies only OSS projects to a clean public repository

$sourceRoot = "C:\Visual Studio Projects\VapeCache"
$targetRoot = "C:\Visual Studio Projects\VapeCache-Public"

# Create target directory
New-Item -ItemType Directory -Force -Path $targetRoot | Out-Null

# Projects to copy (OSS only)
$projectsToCopy = @(
    "VapeCache",
    "VapeCache.Abstractions",
    "VapeCache.Infrastructure",
    "VapeCache.Extensions.Aspire",
    "VapeCache.Benchmarks",
    "VapeCache.Tests",
    "VapeCache.Console"
)

# Directories to copy
$dirsToCopy = @(
    "docs",
    "samples"
)

# Files to copy (root level)
$filesToCopy = @(
    "README.md",
    "LICENSE",
    ".gitignore",
    ".editorconfig",
    "global.json",
    "Directory.Build.props",
    "Directory.Packages.props",
    "VapeCache.sln"  # Will need to modify this
)

Write-Host "Copying OSS projects to $targetRoot..." -ForegroundColor Green

# Copy projects
foreach ($project in $projectsToCopy) {
    $source = Join-Path $sourceRoot $project
    $target = Join-Path $targetRoot $project

    if (Test-Path $source) {
        Write-Host "  Copying $project..." -ForegroundColor Cyan
        Copy-Item -Path $source -Destination $target -Recurse -Force
    }
}

# Copy directories
foreach ($dir in $dirsToCopy) {
    $source = Join-Path $sourceRoot $dir
    $target = Join-Path $targetRoot $dir

    if (Test-Path $source) {
        Write-Host "  Copying $dir..." -ForegroundColor Cyan
        Copy-Item -Path $source -Destination $target -Recurse -Force
    }
}

# Copy root files
foreach ($file in $filesToCopy) {
    $source = Join-Path $sourceRoot $file
    $target = Join-Path $targetRoot $file

    if (Test-Path $source) {
        Write-Host "  Copying $file..." -ForegroundColor Cyan
        Copy-Item -Path $source -Destination $target -Force
    }
}

Write-Host "`nDone! Clean OSS repository ready at: $targetRoot" -ForegroundColor Green
Write-Host "`nNext steps:" -ForegroundColor Yellow
Write-Host "  1. cd '$targetRoot'"
Write-Host "  2. Create new solution file (remove enterprise projects)"
Write-Host "  3. git init && git add . && git commit -m 'Initial commit: VapeCache OSS'"
Write-Host "  4. Create GitHub repo and push"
```

### Step 4: Create Clean Solution File

The copied `.sln` file will reference enterprise projects. Create a new one:

```bash
cd "c:\Visual Studio Projects\VapeCache-Public"

# Remove old solution
rm VapeCache.sln

# Create new solution with OSS projects only
dotnet new sln -n VapeCache

# Add OSS projects
dotnet sln add VapeCache/VapeCache.csproj
dotnet sln add VapeCache.Abstractions/VapeCache.Abstractions.csproj
dotnet sln add VapeCache.Infrastructure/VapeCache.Infrastructure.csproj
dotnet sln add VapeCache.Extensions.Aspire/VapeCache.Extensions.Aspire.csproj
dotnet sln add VapeCache.Benchmarks/VapeCache.Benchmarks.csproj
dotnet sln add VapeCache.Tests/VapeCache.Tests.csproj
dotnet sln add VapeCache.Console/VapeCache.Console.csproj
```

### Step 5: Initialize Public Repository

```bash
cd "c:\Visual Studio Projects\VapeCache-Public"

# Initialize git
git init
git checkout -b main

# Add all files
git add .

# Initial commit (clean history, no secrets)
git commit -m "Initial commit: VapeCache v1.0.0 - High-performance Redis client for .NET

VapeCache is a high-performance, MIT-licensed Redis client for .NET with:
- 5-30% faster than StackExchange.Redis
- Coalesced writes (29% faster SETs)
- Circuit breaker with hybrid cache fallback
- Zero-allocation connection pooling
- OpenTelemetry observability
- Redis modules (Bloom, Search, TimeSeries, JSON)
- .NET Aspire integration

Built with Clean Architecture principles for enterprise .NET applications.
"

# Create GitHub repo (via gh CLI or web interface)
gh repo create haxxornulled/VapeCache --public --source=. --remote=origin

# Push to GitHub
git push -u origin main
```

---

## Security Checklist

Before making the public repository public:

### ✅ Verify No Secrets
```bash
cd "c:\Visual Studio Projects\VapeCache-Public"

# Search for legacy HMAC key
git log --all -p -S "VapeCache-HMAC-Secret"
# Should return NOTHING

# Search for "secret" in all files
rg -i "secret" --type cs
# Should only find comments/docs, no actual keys

# Search for enterprise references
rg -i "enterprise.*license.*key" --type cs
# Should only find docs/comments
```

### ✅ Verify No Enterprise Projects
```bash
# These directories should NOT exist:
Test-Path "VapeCache.Licensing"         # Should be False
Test-Path "VapeCache.LicenseGenerator"  # Should be False
Test-Path "VapeCache.Persistence"       # Should be False
Test-Path "VapeCache.Reconciliation"    # Should be False
Test-Path "ENTERPRISE_STRATEGY.md"      # Should be False
```

### ✅ Verify Solution Builds
```bash
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release
```

### ✅ Verify NuGet Package Metadata
```bash
# Check that OSS packages reference correct repo
dotnet pack VapeCache/VapeCache.csproj -c Release
# Check .nuspec: RepositoryUrl should be github.com/haxxornulled/VapeCache (public)
```

---

## NuGet Publishing Strategy

### Free Tier Packages (Publish from Public Repo)
```bash
cd "c:\Visual Studio Projects\VapeCache-Public"

# Build and pack OSS packages
dotnet pack VapeCache/VapeCache.csproj -c Release -o nupkg
dotnet pack VapeCache.Abstractions/VapeCache.Abstractions.csproj -c Release -o nupkg
dotnet pack VapeCache.Extensions.Aspire/VapeCache.Extensions.Aspire.csproj -c Release -o nupkg

# Publish to NuGet.org
dotnet nuget push nupkg/VapeCache.1.0.0.nupkg --api-key YOUR_API_KEY --source https://api.nuget.org/v3/index.json
dotnet nuget push nupkg/VapeCache.Abstractions.1.0.0.nupkg --api-key YOUR_API_KEY --source https://api.nuget.org/v3/index.json
dotnet nuget push nupkg/VapeCache.Extensions.Aspire.1.0.0.nupkg --api-key YOUR_API_KEY --source https://api.nuget.org/v3/index.json
```

### Enterprise Packages (Publish from Private Repo)
```bash
cd "c:\Visual Studio Projects\VapeCache"

# Build and pack Enterprise packages
dotnet pack VapeCache.Persistence/VapeCache.Persistence.csproj -c Release -o nupkg
dotnet pack VapeCache.Reconciliation/VapeCache.Reconciliation.csproj -c Release -o nupkg

# Publish to NuGet.org
dotnet nuget push nupkg/VapeCache.Persistence.1.0.0.nupkg --api-key YOUR_API_KEY --source https://api.nuget.org/v3/index.json
dotnet nuget push nupkg/VapeCache.Reconciliation.1.0.0.nupkg --api-key YOUR_API_KEY --source https://api.nuget.org/v3/index.json
```

**Note**: Enterprise packages consume `VapeCache.Licensing` as a private package dependency. With ES256 licensing, only the public verification key is in code; private signing keys stay outside source control.

---

## Next Steps

1. **Immediate**: Do NOT push current repo to public GitHub
2. **Today**: Run the copy script to create clean public repo
3. **Today**: Rename current repo to VapeCache-Enterprise (private)
4. **Today**: Initialize and push VapeCache-Public repo
5. **This week**: Publish v1.0.0 NuGet packages
6. **This week**: Launch marketing campaign (Reddit, HN)

---

## Maintenance Workflow

### When Adding OSS Features
1. Develop in private enterprise repo
2. Run copy script to sync to public repo
3. Commit to both repos
4. Push enterprise repo (private)
5. Push public repo (public)

### When Adding Enterprise Features
1. Develop in private enterprise repo only
2. Do NOT copy to public repo
3. Commit and push to enterprise repo (private)

---

## Emergency: If Secret Already Leaked

If you accidentally pushed a signing private key to a public repo:

1. **Immediately rotate the secret**:
   - Rotate signing keys (`VAPECACHE_LICENSE_SIGNING_PRIVATE_KEY_PEM`)
   - Rotate verification key metadata (`VAPECACHE_LICENSE_PUBLIC_KEY_ID`, `VAPECACHE_LICENSE_PUBLIC_KEY_PEM`)
   - Reissue all customer license keys
   - Email all customers with new keys

2. **Scrub git history**:
   ```bash
   git filter-branch --force --index-filter \
     "git rm --cached --ignore-unmatch VapeCache.LicenseGenerator/Program.cs" \
     --prune-empty --tag-name-filter cat -- --all

   git push origin --force --all
   ```

3. **Consider alternative licensing**:
   - Online license validation (phone-home)
   - Hardware fingerprinting
   - Obfuscation (not recommended, security through obscurity)

---

**Status**: Ready to execute. Run the PowerShell script to create the clean public repository.
