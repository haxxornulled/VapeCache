# API and Package Stability Policy

Effective date: 2026-03-09

This policy defines the compatibility contract for OSS consumers.

## Scope

This policy applies to:

- public NuGet package identities
- public APIs in shipped OSS packages
- configuration keys documented in `docs/SETTINGS_REFERENCE.md`

## Package Identity Contract

The OSS package set is:

- `VapeCache.Runtime`
- `VapeCache.Core`
- `VapeCache.Abstractions`
- `VapeCache.Features.Invalidation`
- `VapeCache.Extensions.AspNetCore`
- `VapeCache.Extensions.Aspire`

Rules:

- Package IDs above are frozen for the 1.x line.
- No package rename or package split without a major version.
- If a rename is ever required in 2.x+, a compatibility path must be documented in `docs/PACKAGE_COMPATIBILITY_PLAN.md` before release.

## SemVer Rules

- Patch (`x.y.Z`): bug fixes only, no API breaks.
- Minor (`x.Y.z`): additive API/config only, no API breaks.
- Major (`X.y.z`): breaking changes allowed with migration guidance.

## API Compatibility Rules

For minor and patch releases:

- no removal of public types, members, or enums
- no behavior changes that silently weaken defaults
- obsolete APIs remain supported for at least one minor cycle

For any breaking proposal:

- add an entry to `docs/UPGRADE_NOTES.md`
- add migration examples
- include at least one consumer-facing regression test

## Configuration Compatibility Rules

For minor and patch releases:

- existing config keys continue to bind
- default values do not change in a way that reduces safety/reliability
- renamed keys require compatibility aliases for at least one minor cycle

## Release Gate

A release is blocked if any of the following is true:

- package ID churn is introduced without a major version plan
- public API breaks are detected in minor/patch branches
- docs and quickstart do not match shipped package IDs

