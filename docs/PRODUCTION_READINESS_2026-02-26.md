# Production Readiness Log - 2026-02-26

This document tracks release-hardening work for Enterprise (`origin/main`) and OSS (`oss/main`).

## Status Snapshot

- Enterprise build (`main`): PASS
- Enterprise tests (`main`): PASS (`405` passed, `1` skipped)
- OSS build (`oss/main`): PASS (last isolated validation)
- Repo hygiene cleanup: COMPLETE
- CI hardening: COMPLETE
- Licensing posture review: COMPLETE
- Online revocation control plane: COMPLETE
- ASP.NET Core pipeline hooks (output cache store): COMPLETE

## Step 1 - Stabilize Core Test Reliability

### Goal

Fix the failing scripted transport test and prevent profile-based option overrides from forcing socket coalescing on in scripted/non-socket scenarios.

### Changes

1. Updated multiplexer transport profile application to preserve explicit feature toggles:
   - `VapeCache.Abstractions/Connections/RedisTransportProfiles.cs`
   - `EnableCoalescedSocketWrites` now preserves caller-configured value.
   - `EnableAdaptiveCoalescing` now preserves caller-configured value.
2. Added regression coverage:
   - `VapeCache.Tests/RedisTransportProfilesTests.cs`
   - New test: `ApplyMultiplexerProfile_FullTilt_PreservesExplicitFeatureToggles`.

### Verification

1. Scripted transport regression test:
   - `dotnet test VapeCache.Tests/VapeCache.Tests.csproj -c Release --filter "FullyQualifiedName~RedisCommandExecutorScriptedTests.Executes_and_parses_core_command_surfaces"`
   - Result: PASS
2. Transport profile tests:
   - `dotnet test VapeCache.Tests/VapeCache.Tests.csproj -c Release --filter "FullyQualifiedName~RedisTransportProfilesTests"`
   - Result: PASS
3. Full solution test pass:
   - `dotnet test VapeCache.sln -c Release --no-build`
   - Result: PASS (`370` passed, `1` skipped)

## Next Steps

1. Validate and fix `oss/main` compile health in isolated worktree.
2. Clean artifact hygiene and ignore rules.
3. Tighten CI gates for repeatable release checks.
4. Publish explicit licensing posture and operational checklist.

## Step 2 - OSS Branch Health (Isolated Worktree)

### Goal

Get `oss/main` back to a buildable/testable state after enterprise/OSS drift.

### Worktree Setup

1. Created isolated worktree:
   - `git worktree add ..\VapeCache_oss_check oss/main`
2. Started fix branch in worktree:
   - `oss-readiness-20260226`

### Fixes Applied in OSS Worktree

1. License generator compatibility (HMAC OSS model):
   - `VapeCache.LicenseGenerator/Program.cs`
2. Optional dev settings file handling:
   - `VapeCache.Console/VapeCache.Console.csproj`
   - `appsettings.Development.json` now copied only when file exists.
3. OSS test drift fixes:
   - `VapeCache.Tests/Caching/InMemoryCacheSpillTests.cs`
   - `VapeCache.Tests/Caching/JsonCacheServiceTests.cs`
   - `VapeCache.Tests/Caching/RedisCircuitBreakerHybridCacheTests.cs`
   - `VapeCache.Tests/Console/GroceryStoreComparisonStressTestTests.cs`
4. Same transport toggle reliability fix as Enterprise:
   - `VapeCache.Abstractions/Connections/RedisTransportProfiles.cs`
   - `VapeCache.Tests/RedisTransportProfilesTests.cs`

### Verification (OSS Worktree)

1. `dotnet build VapeCache.sln -c Release`: PASS
2. `dotnet test VapeCache.sln -c Release --no-build`: PASS
   - `316` passed, `1` skipped

## Step 3 - Repository Hygiene

### Goal

Ensure default developer/test artifacts stop polluting git status.

### Changes

Updated `.gitignore`:

- `tmp/`
- `**/TestResults/`
- `tools/VapeCache.DocCommenter/`

### Result

Working tree no longer surfaces local test output and scratch tooling as untracked noise.

## Step 4 - CI Gate Hardening

### Goal

Enforce deterministic release checks on .NET 10 and explicitly protect transport regression paths.

### Changes

Updated `.github/workflows/ci.yml`:

1. `build-test` job now uses `.NET 10` (`10.0.x`) instead of `8.0.x`.
2. Added explicit `Build` step:
   - `dotnet build -c Release --no-restore VapeCache.sln`
3. Unit tests now run from built outputs:
   - `dotnet test -c Release --no-build VapeCache.sln`
4. Added explicit transport regression checks:
   - scripted command-surface test
   - transport profile test suite

## Step 5 - Licensing Posture Review

### What Is Solid

1. Signature validation and claims enforcement are present in Enterprise licensing runtime:
   - `VapeCache.Licensing/LicenseValidator.cs`
2. Enterprise feature gates are enforced where expected:
   - `VapeCache.Persistence/PersistenceServiceExtensions.cs`
   - `VapeCache.Reconciliation/RedisReconciliationExtensions.cs`
3. Licensing-focused tests are green on Enterprise:
   - `18/18` passed in prior validation run.

### Remaining Risk/Hardening Gaps

1. No telemetry-backed license abuse detection loop yet.
2. Signed issuance ledger + operator-facing incident dashboards are not implemented yet.
3. Token issuance still lives in this repo; external authority repo split is planned.

### Operational Docs Added

1. `docs/LICENSE_OPERATIONS_RUNBOOK.md`
   - key rotation process
   - emergency compromise response
   - baseline monitoring expectations
2. `docs/LICENSE_CONTROL_PLANE.md`
   - endpoint contract and runtime configuration
3. `docs/LICENSE_GENERATOR_EXTERNALIZATION.md`
   - extraction plan for private signing/issuance code

## Step 6 - Online Revocation / Kill-Switch Service

### Goal

Implement a dedicated .NET 10 control-plane service and wire runtime revocation checks.

### Changes

1. Added `VapeCache.Licensing.ControlPlane` (ASP.NET Core, Autofac, Serilog):
   - API-key protected revoke/activate endpoints
   - status endpoint contract consumed by runtime checker
   - atomic file-backed state persistence
2. Added runtime revocation client in `VapeCache.Licensing`:
   - `LicenseRevocationClient`
   - `LicenseRevocationRuntimeOptions`
   - `LicenseRevocationCheckResult`
3. Added centralized strict gate:
   - `LicenseFeatureGate.RequireEnterpriseFeature(...)`
   - enforces required key + feature entitlement + revocation decision.
4. Updated enterprise extension wiring:
   - `VapeCache.Persistence/PersistenceServiceExtensions.cs`
   - `VapeCache.Reconciliation/RedisReconciliationExtensions.cs`
5. Hardened verifier env override behavior:
   - default ignore of verifier env overrides unless `VAPECACHE_LICENSE_ALLOW_VERIFIER_ENV_OVERRIDE=true`.

### Verification

1. `dotnet build VapeCache.sln -c Release`: PASS
2. `dotnet test VapeCache.Tests/VapeCache.Tests.csproj -c Release --filter "FullyQualifiedName~Licensing"`: PASS (`24` passed)

## Step 7 - ASP.NET Core Output Caching Hooks

### Goal

Provide first-class ASP.NET Core pipeline integration for MVC, Minimal APIs, and Blazor output caching using VapeCache storage.

### Changes

1. Added package project:
   - `VapeCache.Extensions.AspNetCore`
2. Implemented `IOutputCacheStore` backed by `ICacheService`:
   - `VapeCacheOutputCacheStore`
   - `VapeCacheOutputCacheStoreOptions`
3. Added service + pipeline extensions:
   - `AddVapeCacheOutputCaching(...)`
   - `UseVapeCacheOutputCaching()`
   - `CacheWithVapeCache()` for minimal APIs
4. Added Aspire fluent hook:
   - `WithAspNetCoreOutputCaching(...)`
5. Added docs:
   - `docs/ASPNETCORE_PIPELINE_CACHING.md`
   - updated quickstart/configuration/API reference/package index docs.

### Verification

1. `dotnet build VapeCache.sln -c Release`: PASS
2. `dotnet test VapeCache.Tests/VapeCache.Tests.csproj -c Release --filter "FullyQualifiedName~AspNetCore|FullyQualifiedName~AspireExtensionsTests"`: PASS (`12` passed)
3. `dotnet test VapeCache.sln -c Release --no-build`: PASS (`381` passed, `1` skipped)

## Final Production Checklist (Current)

### Enterprise (`origin/main`)

1. Build: PASS
2. Tests: PASS (`405` passed, `1` skipped)
3. CI gates: HARDENED
4. Repo hygiene: CLEAN rules applied

### OSS (`oss/main`) via isolated verification

1. Build: PASS
2. Tests: PASS (`316` passed, `1` skipped)
3. Branch drift fixes prepared in dedicated OSS worktree branch

## Remaining Actions Before Final Release Tag

1. Commit/push Enterprise hardening changes from this branch.
2. Commit/push OSS hardening changes from `oss-readiness-20260226` to `oss/main`.
3. Decide whether to ship with offline-only licensing or implement revocation endpoint in this release.

## Step 8 - Revalidation Sweep (Build/Test/Packaging/Security)

### Goal

Run a fresh production-readiness gate after spill diagnostics and ASP.NET Core pipeline integration updates.

### Verification Results

1. `dotnet build VapeCache.sln -c Release`: PASS (`0` warnings / `0` errors)
2. `dotnet test VapeCache.Tests/VapeCache.Tests.csproj -c Release --no-build`: PASS (`390` passed, `1` skipped)
3. `dotnet test VapeCache.PerfGates.Tests/VapeCache.PerfGates.Tests.csproj -c Release`: PASS (`7` passed)
4. `dotnet list VapeCache.sln package --vulnerable --include-transitive`: PASS (no vulnerable packages)
5. `dotnet pack VapeCache.sln -c Release --no-build`: PASS with expected non-packable warning for control-plane app

### Readiness Findings (Open)

1. Redis 8.4 handshake TODO remains:
   - `VapeCache.Infrastructure/Connections/RedisConnectionFactory.cs:253`
2. Hosted-service lifecycle consistency is incomplete in Console host:
   - `VapeCache.Console/Plugins/PluginDemoHostedService.cs:13`
   - `VapeCache.Console/GroceryStore/GroceryStoreStressTest.cs:19`
   - `VapeCache.Console/Hosting/RedisConnectionPoolReaperHostedService.cs:12`
   - `VapeCache.Console/Hosting/LiveDemoHostedService.cs:15`
   - `VapeCache.Console/Hosting/RedisSanityCheckHostedService.cs:15`
3. Working tree is not release-clean (many modified/untracked files) and needs commit discipline before tagging.

### Notes

1. Console output policy check remains compliant: `Console.WriteLine` usage is limited to GroceryStore paths.
2. Runtime service-locator anti-pattern (`HttpContext.RequestServices.GetService`) not found in app code.

## Step 9 - Blocker Closure Revalidation (Handshake + Hosted Lifecycle)

### Goal

Close the two remaining production blockers identified in Step 8 and re-run release verification.

### Changes

1. Redis HELLO handshake hardening:
   - `VapeCache.Infrastructure/Connections/RedisConnectionFactory.cs`
   - `VapeCache.Infrastructure/Connections/RedisRespProtocol.cs`
   - Enabled `HELLO 2` negotiation path with safe fallback to legacy AUTH/SELECT on protocol parse errors.
   - Hardened HELLO response skipping to consume RESP2/RESP3 maps/attributes/sets/push values.
2. Hosted lifecycle consistency:
   - `VapeCache.Console/GroceryStore/GroceryStoreStressTest.cs`
   - `VapeCache.Console/Hosting/RedisConnectionPoolReaperHostedService.cs`
   - `VapeCache.Console/Hosting/LiveDemoHostedService.cs`
   - `VapeCache.Console/Hosting/RedisSanityCheckHostedService.cs`
   - All now implement `IHostedLifecycleService` while retaining `BackgroundService` execution model.
3. Regression tests added:
   - `VapeCache.Tests/Connections/RedisRespProtocolTests.cs`
   - Added coverage for RESP3 map HELLO payload, attribute-wrapped payload, and error handling.

### Verification Results

1. `dotnet test VapeCache.Tests/VapeCache.Tests.csproj -c Release --filter "FullyQualifiedName~RedisRespProtocolTests"`: PASS (`6` passed)
2. `dotnet build VapeCache.sln -c Release`: PASS (`0` warnings / `0` errors)
3. `dotnet test VapeCache.Tests/VapeCache.Tests.csproj -c Release --no-build --blame-hang --blame-hang-timeout 5m`: PASS (`393` passed, `1` skipped)
4. `dotnet test VapeCache.PerfGates.Tests/VapeCache.PerfGates.Tests.csproj -c Release`: PASS (`7` passed)

### Blocker Status

1. Redis 8.4 handshake TODO risk: CLOSED
2. Hosted-service lifecycle inconsistency: CLOSED
3. Working tree cleanliness before release tag: STILL OPEN (needs commit/staging discipline)

## Step 10 - Fresh Release Revalidation (Current Branch State)

### Goal

Capture an up-to-date release gate snapshot after the latest integration/test expansion.

### Verification Results

1. `dotnet build VapeCache.sln -c Release -v minimal`: PASS (`0` warnings / `0` errors)
2. `dotnet test VapeCache.Tests/VapeCache.Tests.csproj -c Release --no-build -v minimal`: PASS (`405` passed, `1` skipped)
3. `dotnet test VapeCache.PerfGates.Tests/VapeCache.PerfGates.Tests.csproj -c Release -v minimal`: PASS (`7` passed)
4. `dotnet list VapeCache.sln package --vulnerable --include-transitive`: PASS (no vulnerable packages)
5. `dotnet pack VapeCache.sln -c Release --no-build -v minimal`: PASS with expected non-packable warning for `VapeCache.Licensing.ControlPlane`

### Current Release Findings

1. Build/test/perf/security/packaging gates are green on the current branch state.
2. Working tree cleanliness before release tag remains OPEN and requires staged, intentional commit grouping.
