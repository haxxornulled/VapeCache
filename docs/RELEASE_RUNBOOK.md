# Release Runbook (OSS)

This runbook standardizes OSS release execution to reduce single-operator risk.

## Preconditions

- branch is `main`
- required checks are green
- release version is already set in packable projects
- no unresolved P0 issues

## Required Access

- git push access for the public OSS repo
- NuGet.org publish credentials
- local environment with `pwsh` and `.NET 10 SDK`

## Remote Expectations

- package metadata must continue to point to `https://github.com/haxxornulled/VapeCache`
- local clone should keep the public OSS remote available as `oss`
- if `origin` targets a private mirror, release tags and release notes still need to land in `oss`

## Release Steps

1. Confirm local/remote sync.
   - `git fetch origin --tags`
   - `git fetch oss --tags`
   - `git status`
2. Run release verification:
   - `pwsh ./tools/release-check.ps1 -Configuration Release`
3. Pack release artifacts:
   - `pwsh ./tools/pack-release-packages.ps1 -PackageVersion 1.2.4`
4. Publish to NuGet.org:
   - set `NUGET_API_KEY` in the shell or pass `-ApiKey`
   - `pwsh ./tools/publish-release-packages.ps1 -PackageVersion 1.2.4`
5. Verify NuGet push logs include all OSS package IDs:
   - `VapeCache.Core`
   - `VapeCache.Abstractions`
   - `VapeCache.Features.Invalidation`
   - `VapeCache.Runtime`
   - `VapeCache.Extensions.DependencyInjection`
   - `VapeCache.Extensions.AspNetCore`
   - `VapeCache.Extensions.Aspire`
6. Push the release commit/tag to the public OSS repo.
7. Verify consumer install from nuget.org.

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
- rerun `tools/release-check.ps1`
- repack with the same version after fixing the issue

If release fails during publish:

- identify exact package that failed
- patch release tooling to preserve dependency-safe publish order
- rerun `tools/publish-release-packages.ps1` with `-SkipPackageIds` if already-published packages should be skipped

If release tag/repo state is stale or misaligned:

- correct the public `oss` remote first
- push only the intended release commit/tag
- re-verify package metadata points at `haxxornulled/VapeCache`

## Bus-Factor Controls

- runbook must be kept current
- at least one backup maintainer should be able to execute these steps without private tribal knowledge
