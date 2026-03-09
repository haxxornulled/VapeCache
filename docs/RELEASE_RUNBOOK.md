# Release Runbook (OSS)

This runbook standardizes OSS release execution to reduce single-operator risk.

## Preconditions

- branch is `main`
- required checks are green
- release version is already set in packable projects
- no unresolved P0 issues

## Required Access

- GitHub repo write access
- GitHub Actions workflow dispatch access
- NuGet trusted publishing policy configured for:
  - owner: `haxxornulled`
  - repo: `haxxornulled/VapeCache`
  - workflow: `.github/workflows/build.yml`
  - environment: `production`

## Release Steps

1. Confirm local/remote sync.
2. Trigger release workflow:
   - workflow: `build.yml`
   - input: `release_tag` (example: `v1.2.1`)
3. Monitor run to completion.
4. Verify NuGet push logs include all OSS package IDs:
   - `VapeCache.Core`
   - `VapeCache.Abstractions`
   - `VapeCache.Features.Invalidation`
   - `VapeCache.Runtime`
   - `VapeCache.Extensions.AspNetCore`
   - `VapeCache.Extensions.Aspire`
5. Verify GitHub release assets and checksums.
6. Verify consumer install from nuget.org.

## Post-Release Verification

Run from a clean temp project:

```bash
dotnet new webapi -n Smoke
cd Smoke
dotnet add package VapeCache.Extensions.AspNetCore --source https://api.nuget.org/v3/index.json
dotnet restore
dotnet build -c Release
```

## Failure Handling

If release fails before publish:

- fix on branch
- rerun workflow for same tag

If release fails during publish:

- identify exact package that failed
- patch release tooling to preserve dependency-safe publish order
- rerun workflow

If release assets are stale/misaligned:

- delete only incorrect assets from GitHub release
- rerun workflow and re-verify checksums

## Bus-Factor Controls

- runbook must be kept current
- at least one backup maintainer should be able to execute these steps without private tribal knowledge
