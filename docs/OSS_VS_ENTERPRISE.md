# OSS vs Enterprise

This document defines the product boundary for the public VapeCache repository.

Public home:

- Website: `https://vapecache.net`
- OSS repository: `https://github.com/haxxornulled/VapeCache`

The goal is simple:
- OSS must remain production-usable for normal application workloads.
- Enterprise adds operational leverage for higher-load and multi-environment fleet operation.

## Licensing

VapeCache is source-available under BUSL-1.1.

- Internal production use is allowed under the Additional Use Grant.
- Managed caching/database services and commercial caching/database infrastructure products require a commercial license.
- The code converts to Apache-2.0 on `2029-03-11`.

This document still uses "OSS" as a product-boundary shorthand for the public runtime surface, not as a claim that the code is open source.

## OSS Includes

This repository ships the OSS runtime and integrations, including:

- core runtime
- abstractions
- invalidation
- DI/composition extensions
- ASP.NET Core integration
- Aspire integration
- multiplexed transport
- baseline observability
- normal developer ergonomics

## Enterprise Includes

Enterprise capabilities are focused on operational leverage:

- adaptive autoscaling of multiplexed lanes
- durable spill persistence
- reconciliation and post-outage write replay
- control-plane/admin features
- advanced fleet/load operations behavior

## Boundary Clarifications

- Multiplexing itself is OSS.
- Adaptive autoscaling of multiplexed lanes is Enterprise.
- Enterprise boundaries are about operating at larger scale with more control and lower operational risk, not crippling normal OSS usability.

## Practical Interpretation

If you are building and shipping a typical .NET API or service with Redis-backed caching, OSS is intended to be enough.

Enterprise is for teams that need stronger operational controls across sustained high load, outages, recovery workflows, and fleet-level governance.

## Demo Host Direction

The historical console host is no longer part of the active OSS surface.

Going forward, the right replacement is not another ad hoc demo app. It should be a deliberate demo host that proves:

- which OSS packages are production-ready
- which integration seams are wired end to end
- which features are intentionally Enterprise-only

See [DEMO_HOST_BLUEPRINT.md](DEMO_HOST_BLUEPRINT.md) for the current design direction.
