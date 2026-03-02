# VapeCache Repository Split - Quick Start

## 🚀 TL;DR - Run These Commands

### Step 1: Create Clean Public Repo (5 minutes)

```powershell
# Run the automated copy script
cd "c:\Visual Studio Projects\VapeCache"
.\copy-to-public.ps1

# Verify security (MUST PASS before publishing)
.\verify-public-repo.ps1

# If verification passes, initialize git
cd "c:\Visual Studio Projects\VapeCache-Public"
git init
git checkout -b main
git add .
git commit -m "Initial commit: VapeCache v1.0.0 - High-performance Redis client for .NET

VapeCache is a high-performance, MIT-licensed Redis client for .NET with:
- 5-30% faster than StackExchange.Redis
- Coalesced writes (29% faster SETs)
- Circuit breaker with hybrid cache fallback
- Zero-allocation connection pooling
- OpenTelemetry observability
- Redis modules (Bloom, Search, TimeSeries, JSON)
- .NET Aspire integration
"

# Create GitHub repo (requires gh CLI)
gh repo create haxxornulled/VapeCache --public --source=. --remote=origin

# Push to GitHub
git push -u origin main
```

### Step 2: Rename Current Repo to Enterprise (2 minutes)

```bash
# In current repo
cd "c:\Visual Studio Projects\VapeCache"

# Update remote URL
git remote set-url origin https://github.com/haxxornulled/VapeCache-Enterprise.git

# Push (and set repo to PRIVATE on GitHub)
git push -u origin main
```

### Step 3: Publish to NuGet (10 minutes)

```bash
# OSS packages (from public repo)
cd "c:\Visual Studio Projects\VapeCache-Public"
dotnet pack VapeCache/VapeCache.csproj -c Release -o nupkg
dotnet pack VapeCache.Abstractions/VapeCache.Abstractions.csproj -c Release -o nupkg
dotnet pack VapeCache.Extensions.Aspire/VapeCache.Extensions.Aspire.csproj -c Release -o nupkg

# Get API key from https://www.nuget.org/account/apikeys
dotnet nuget push nupkg/*.nupkg --api-key YOUR_KEY --source https://api.nuget.org/v3/index.json

# Enterprise packages (from private repo)
cd "c:\Visual Studio Projects\VapeCache"
dotnet pack VapeCache.Persistence/VapeCache.Persistence.csproj -c Release -o nupkg
dotnet pack VapeCache.Reconciliation/VapeCache.Reconciliation.csproj -c Release -o nupkg
dotnet nuget push nupkg/*.nupkg --api-key YOUR_KEY --source https://api.nuget.org/v3/index.json
```

---

## ⚠️ CRITICAL WARNINGS

### 🔴 DO NOT
- ❌ Push current repo to public GitHub (contains HMAC secret in git history)
- ❌ Share HMAC secret key with anyone
- ❌ Include enterprise projects in public repo
- ❌ Skip the `verify-public-repo.ps1` security check

### ✅ DO
- ✅ Run `verify-public-repo.ps1` before publishing
- ✅ Keep VapeCache-Enterprise repo PRIVATE
- ✅ Review git log before pushing: `git log --oneline`
- ✅ Double-check GitHub repo visibility settings

---

## 📁 Repository Structure After Split

### Public Repo: `github.com/haxxornulled/VapeCache` (PUBLIC)
```
VapeCache-Public/
├── VapeCache/                    # Main package (MIT)
├── VapeCache.Abstractions/       # Interfaces (MIT)
├── VapeCache.Infrastructure/     # Core implementation (MIT)
├── VapeCache.Extensions.Aspire/  # Aspire integration (Apache-2.0)
├── VapeCache.Benchmarks/         # Performance benchmarks
├── VapeCache.Tests/              # Test suite
├── VapeCache.Console/            # Demo/examples
├── docs/                         # Documentation
├── samples/                      # Sample code
├── README.md
├── LICENSE (MIT)
└── VapeCache.sln
```

### Private Repo: `github.com/haxxornulled/VapeCache-Enterprise` (PRIVATE)
```
VapeCache/
├── [All public repo contents]
├── VapeCache.Persistence/        # Enterprise package (PRIVATE source)
├── VapeCache.Reconciliation/     # Enterprise package (PRIVATE source)
├── VapeCache.Application/        # Internal utilities
├── ENTERPRISE_STRATEGY.md        # Business model (PRIVATE)
└── [Full solution]
```

### Private Licensing Repo: `github.com/haxxornulled/VapeCache.Licensing` (PRIVATE)
```
VapeCache.Licensing/
├── VapeCache.Licensing/              # License validation runtime package
├── VapeCache.LicenseGenerator/       # Key generation tool
├── VapeCache.Licensing.ControlPlane/ # Revocation control-plane service
└── VapeCache.ApiTests/               # Licensing API integration tests
```

---

## 🔐 Security Verification

Before publishing public repo, verify:

```powershell
cd "c:\Visual Studio Projects\VapeCache-Public"

# 1. No HMAC secret
rg "VapeCache-HMAC-Secret" --type cs
# Should return: NO MATCHES

# 2. No enterprise projects
ls VapeCache.Licensing
# Should return: ERROR (path not found)

# 3. No LicenseValidator instantiation
rg "new LicenseValidator" --type cs
# Should return: NO MATCHES

# 4. Build succeeds
dotnet build --configuration Release
# Should return: Build succeeded

# 5. Tests pass
dotnet test --configuration Release
# Should return: All tests passed (or expected failures)
```

---

## 📦 What Gets Published to NuGet

### Free Tier (Public Source)
| Package | License | Source Code | Price |
|---------|---------|-------------|-------|
| VapeCache | MIT | Public GitHub | FREE |
| VapeCache.Abstractions | MIT | Public GitHub | FREE |
| VapeCache.Extensions.Aspire | Apache-2.0 | Public GitHub | FREE |

### Enterprise Tier (Private Source)
| Package | License | Source Code | Price |
|---------|---------|-------------|-------|
| VapeCache.Persistence | Proprietary | Private (NuGet binary only) | $499/mo |
| VapeCache.Reconciliation | Commercial | Private (NuGet binary only) | $499/mo |

**Note**: Enterprise customers get access to private repo source code as part of their subscription.

---

## 🎯 Launch Timeline

| When | Task | Status |
|------|------|--------|
| **Today** | Create public repo with `copy-to-public.ps1` | ⏳ Ready |
| **Today** | Rename current repo to VapeCache-Enterprise | ⏳ Ready |
| **This Week** | Publish v1.0.0 to NuGet.org | ⏳ Ready |
| **This Week** | Reddit/HN announcement | ⏳ Ready |
| **This Week** | Set up Stripe billing | 🔲 TODO |
| **This Month** | Launch landing page (vapecache.com) | 🔲 TODO |
| **Q1 2026** | First 10 Enterprise customers ($4,990 MRR) | 🎯 Goal |

---

## 📞 Next Steps

1. **Right now**: Run `copy-to-public.ps1`
2. **5 minutes later**: Run `verify-public-repo.ps1` (MUST PASS)
3. **10 minutes later**: Initialize git and push to GitHub
4. **This week**: Publish to NuGet.org
5. **This week**: Announce on Reddit/HN

**For detailed instructions, see**: [READY_FOR_LAUNCH.md](READY_FOR_LAUNCH.md)

---

## 🆘 If Something Goes Wrong

### "verify-public-repo.ps1 failed!"
- Check which security test failed
- Review the error messages
- DO NOT publish until all checks pass
- See REPOSITORY_SETUP.md for troubleshooting

### "Build failed in public repo!"
- Ensure all OSS projects were copied
- Check for missing dependencies
- Run `dotnet restore` first

### "I accidentally pushed the HMAC secret!"
- **IMMEDIATELY** rotate the secret key
- Regenerate all customer license keys
- Follow emergency procedures in REPOSITORY_SETUP.md

---

**Ready to launch!** 🚀

Start with: `.\copy-to-public.ps1`
