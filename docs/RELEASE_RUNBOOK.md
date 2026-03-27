# Release Runbook (OSS)

This runbook standardizes OSS release execution to reduce single-operator risk.

## Preconditions

- branch is `main`
- required checks are green
- release version is already set in packable projects
  - recommended path: `./tools/bump-package-versions.ps1 -PackageVersion <package-version>`
- package packing is intentionally serialized by default (`-MaxCpuCount 1`) to keep release artifacts deterministic across the current project-reference graph
- no unresolved P0 issues

## Required Access

- git push access for the public OSS repo
- NuGet.org publish credentials (`NUGET_API_KEY`) with push access for all `VapeCache.*` package IDs
- GitHub Packages token (`GITHUB_PACKAGES_TOKEN` or `GITHUB_TOKEN`) with `write:packages` and `read:packages` scopes (and repo access for private repos if needed)
- local environment with PowerShell (`pwsh` preferred, `powershell.exe` supported) and `.NET 10 SDK`

## Remote Expectations

- package metadata must continue to point to `https://github.com/haxxornulled/VapeCache`
- local clone should keep the public OSS remote available as `oss`
- if `origin` targets a private mirror, release tags and release notes still need to land in `oss`

## Release Steps

Preferred one-command path:

```powershell
./tools/bump-package-versions.ps1 -PackageVersion <package-version>
./tools/release-orchestrator.ps1 -Configuration Release -PackageVersion <package-version>
```

GitHub Actions options:
- `.github/workflows/ci.yml`: pull request and `main` branch validation (`release-check` with `-SkipPack -UsePublicSourcesOnly`)
- `.github/workflows/publish-packages.yml`: release publish workflow (tag-triggered or manual `workflow_dispatch`)
- Trigger publish via `workflow_dispatch` (optional `packageVersion`) or by pushing a `v*` tag.

The orchestrator enforces preflight checks, release-check gates, package packing + smoke tests, feed publishing, remote sync, tag push, and GitHub release updates.

## Script Roles

Use `tools/release-orchestrator.ps1` as the primary entry point.
The other scripts support a narrower part of the same release flow:

- `tools/release-orchestrator.ps1`: full OSS release flow, including remote sync, pack, smoke, publish, tags, and GitHub releases
- `tools/bump-package-versions.ps1`: updates all packable OSS package versions in one pass before release orchestration
- `tools/release-check.ps1`: restore, build, tests, optional audits, and package smoke validation
- `tools/pack-release-packages.ps1`: produce release `.nupkg` artifacts
- `tools/publish-release-packages.ps1`: publish an already-packed artifact set to a package feed
- `tools/package-smoke.ps1`: verify a consumer can restore and build against one packed package
- `tools/release-package-manifest.ps1`: single source of truth for release package list, smoke package list, versions, and branding checks
- `tools/release-common.ps1`: shared release helper functions used by the executable scripts

This split is intentional:

- manifest data lives in one place
- shared release mechanics live in one place
- runnable scripts stay focused on one operator-facing job

1. Confirm local/remote sync.
   - `git fetch origin --tags`
   - `git fetch oss --tags`
   - `git status`
2. Bump package versions in one pass:
   - `./tools/bump-package-versions.ps1 -PackageVersion <package-version>`
3. Run release verification:
   - `./tools/release-check.ps1 -Configuration Release`
4. Pack release artifacts:
   - `./tools/pack-release-packages.ps1 -Configuration Release -PackageVersion <package-version>`
   - if restore must come from an explicit cache or mirror, pass one or more `-RestoreSource` values and optionally `-IgnoreFailedRestoreSources`
   - example: `./tools/pack-release-packages.ps1 -Configuration Release -PackageVersion <package-version> -RestoreSource "$HOME\.nuget\packages" -IgnoreFailedRestoreSources`
5. Publish to NuGet.org:
   - set `NUGET_API_KEY` in the shell or pass `-ApiKey`
   - `./tools/publish-release-packages.ps1 -PackageVersion <package-version>`
   - if you receive HTTP 403, verify the key is valid and has owner/package permissions for every `VapeCache.*` package
6. Publish to GitHub Packages:
   - set `GITHUB_PACKAGES_TOKEN` (or `GITHUB_TOKEN`) in the shell
   - `./tools/publish-release-packages.ps1 -PackageVersion <package-version> -Source https://nuget.pkg.github.com/haxxornulled/index.json -ApiKey $env:GITHUB_PACKAGES_TOKEN`
   - if you receive HTTP 403, verify token scopes include `write:packages`
7. Verify push logs (both feeds) include all OSS package IDs:
   - `VapeCache.Core`
   - `VapeCache.Abstractions`
   - `VapeCache.Features.Invalidation`
   - `VapeCache.Runtime`
   - `VapeCache.Extensions.DependencyInjection`
   - `VapeCache.Extensions.AdminAuth`
   - `VapeCache.Extensions.Logging`
   - `VapeCache.Extensions.PubSub`
   - `VapeCache.Extensions.Streams`
   - `VapeCache.Extensions.EntityFrameworkCore`
   - `VapeCache.Extensions.EntityFrameworkCore.OpenTelemetry`
   - `VapeCache.Extensions.AspNetCore`
   - `VapeCache.Extensions.Aspire`
8. Push the release commit/tag to the public OSS repo.
9. Verify consumer install from nuget.org and GitHub Packages.

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


