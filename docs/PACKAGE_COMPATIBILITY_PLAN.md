# Package Compatibility Plan

This plan governs package compatibility decisions for OSS releases.

## Problem Statement

Consumers need stable package identity and predictable restore behavior.

Recent churn (`VapeCache` -> `VapeCache.Runtime`) showed that even a correct rename can cause install friction if docs, workflows, and dependency publication are not synchronized.

## Canonical Package Entry Point

Canonical runtime package for OSS is:

- `VapeCache.Runtime`

All install docs and quickstarts must use this package ID.

## Compatibility Commitments

- No further runtime package rename in 1.x.
- Keep `VapeCache.Runtime` as the primary package for the full 1.x line.
- Keep transitive dependency chain resolvable from nuget.org (`VapeCache.Core`, `VapeCache.Abstractions`, etc.).

## Legacy Name Handling

Current limitation:

- `VapeCache` package ID is externally owned and cannot be republished by this repository.

Mitigations:

- explicit install guidance in README/QuickStart
- migration note in `docs/UPGRADE_NOTES.md`
- consumer validation workflow to catch dependency graph regressions early

## Future Major Version Strategy (2.x+)

If package topology changes:

- publish a compatibility matrix before release
- provide side-by-side package mapping
- ship migration snippets for common installs

Example mapping template:

- `Old.Package.Id` -> `New.Package.Id`
- min supported version
- required code/config changes

