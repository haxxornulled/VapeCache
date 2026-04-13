# POS Search Demo Status

The historical POS search demo depended on the removed `VapeCache.Console` host and is not part of the current OSS repository surface.

## Current State

These historical commands are no longer valid in this repo:

- `VapeCache.Console/run-pos-search-demo.ps1`
- `VapeCache.Console/run-pos-search-load.ps1`
- `dotnet run --project VapeCache.Console`

## Maintainer Guidance

Do not update this document as if the old console surface still exists.
If POS search returns as an OSS feature, anchor it in the replacement host plan from [DEMO_HOST_BLUEPRINT.md](DEMO_HOST_BLUEPRINT.md), restore the code, and replace this archival note with current run instructions and ownership tests in the same PR.
