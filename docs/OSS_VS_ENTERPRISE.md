# OSS vs Enterprise

This document defines the product boundary for the public VapeCache repository.

The goal is simple:
- OSS must remain production-usable for normal application workloads.
- Enterprise adds operational leverage for higher-load and multi-environment fleet operation.

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
- control-plane/admin/licensing features
- advanced fleet/load operations behavior

## Boundary Clarifications

- Multiplexing itself is OSS.
- Adaptive autoscaling of multiplexed lanes is Enterprise.
- Enterprise boundaries are about operating at larger scale with more control and lower operational risk, not crippling normal OSS usability.

## Practical Interpretation

If you are building and shipping a typical .NET API or service with Redis-backed caching, OSS is intended to be enough.

Enterprise is for teams that need stronger operational controls across sustained high load, outages, recovery workflows, and fleet-level governance.
