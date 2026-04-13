# Grocery Store Demo Status

The historical `VapeCache.Console` grocery-store demo is not part of the current OSS repository surface.

## What Changed

The old console host and its related stress/demo scripts were removed during repository scope reduction.

That means these historical commands are no longer valid in the current repo:

- `dotnet run --project VapeCache.Console`
- `tools/run-grocery-head-to-head.ps1`
- `tools/run-grocery-live.ps1`
- `VapeCache.Console/run-grocery-dogfood.ps1`
- `VapeCache.Console/run-grocery-stampede.ps1`

## What To Use Instead

For current performance validation use:

- [BENCHMARKING.md](BENCHMARKING.md)
- [VapeCache.Benchmarks/README.md](../VapeCache.Benchmarks/README.md)

For the replacement public proof surface use:

- [DEMO_HOST_BLUEPRINT.md](DEMO_HOST_BLUEPRINT.md)

For current runtime architecture use:

- [TRANSPORT_MUX_AUTOSCALER_DEEP_DIVE.md](TRANSPORT_MUX_AUTOSCALER_DEEP_DIVE.md)
- [MUX_MAINTAINER_GUIDE.md](MUX_MAINTAINER_GUIDE.md)

## Maintainer Note

This file remains only to prevent dead links from resolving to stale instructions.
If the grocery demo is ever reintroduced, replace this archival note with fresh end-to-end documentation in the same change that restores the code.
