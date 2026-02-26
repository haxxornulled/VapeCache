# Production Readiness Log - 2026-02-26

This document tracks release-hardening work for Enterprise (`origin/main`) and OSS (`oss/main`).

## Status Snapshot

- Enterprise build (`main`): PASS
- Enterprise tests (`main`): PASS (`370` passed, `1` skipped)
- OSS build (`oss/main`): IN PROGRESS
- Repo hygiene cleanup: IN PROGRESS
- CI hardening: IN PROGRESS
- Licensing posture review: IN PROGRESS

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

1. No online revocation/kill-switch path yet (offline validation only).
2. No telemetry-backed license abuse detection loop yet.
3. Signed issuance ledger + operator-facing incident dashboards are not implemented yet.

### Operational Docs Added

1. `docs/LICENSE_OPERATIONS_RUNBOOK.md`
   - key rotation process
   - emergency compromise response
   - baseline monitoring expectations

## Final Production Checklist (Current)

### Enterprise (`origin/main`)

1. Build: PASS
2. Tests: PASS (`370` passed, `1` skipped)
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
