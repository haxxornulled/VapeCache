# Demo Host Blueprint

This document replaces the old "console host" idea with a deliberate demo-host plan.

The goal is not to bring back a toy app.
The goal is to ship one public-facing proof surface that makes it obvious what VapeCache can do today, what is OSS, what is Enterprise, and how the packages fit together end to end.

## Why The Old Console Host Failed

The old console host grew opportunistically.

It mixed together:

- experiments
- benchmarks
- stress demos
- plugin ideas
- host-specific options

That made it expensive to maintain and hard for outsiders to tell what was real product surface versus internal exploration.

## What The New Demo Host Must Do

The replacement should be a real product-grade demo host with these constraints:

1. It must prove every active OSS package at least once.
2. It must clearly label Enterprise-only capabilities instead of implying they are available in OSS.
3. It must be runnable by contributors without hidden tribal knowledge.
4. It must double as onboarding documentation for maintainers.
5. It must be safe to link from `vapecache.net`.

## Proposed Shape

Use a web-first demo instead of a console-first host.

Recommended structure:

- `VapeCache.DemoHost`
  - ASP.NET Core host
  - clear setup page
  - live health/status pages
  - package-by-package demo routes
- `VapeCache.DemoHost.Tests`
  - smoke/integration tests for the demo host contract

The demo host should be small enough to understand, but broad enough to prove the platform.

## Required Feature Matrix

### Core runtime

- hybrid get/set/remove flows
- circuit breaker visibility
- in-memory fallback visibility
- stampede protection demo
- named caches

### `VapeCache.Features.Invalidation`

- tag invalidation
- zone invalidation
- lazy stale-drop demonstration

### `VapeCache.Extensions.DistributedCache`

- `IDistributedCache` interoperability route
- clear explanation of bridge versus native runtime

### `VapeCache.Extensions.AspNetCore`

- output-cache policy demo
- route-level cache policy examples

### `VapeCache.Extensions.Aspire`

- app-host wiring
- health/admin endpoint visibility

### `VapeCache.Extensions.Logging`

- structured logging
- OpenTelemetry wiring
- documented local sink experience

### `VapeCache.Extensions.PubSub`

- publish/subscribe sample route or worker
- reconnect/resubscribe visibility

### `VapeCache.Extensions.Streams`

- idempotent producer sample
- observable success/failure state

### `VapeCache.Extensions.EntityFrameworkCore`

- second-level cache sample with deterministic invalidation path

### `VapeCache.Extensions.EntityFrameworkCore.OpenTelemetry`

- cache hit/miss telemetry sample for EF path

### `VapeCache.Extensions.KeyDB`

- explicit backend-registration variant

### `VapeCache.Features.Search`

- HASH projection build path
- RediSearch query path
- invalidation-aware result story

## UX Requirements

The demo host should make the status of the platform obvious.

Suggested sections:

- Overview
- OSS package matrix
- Active backend status
- Health and resilience
- Search and invalidation
- Pub/Sub and streams
- EF Core integration
- Observability
- Enterprise-only features

Each section should answer:

- what package enables this
- what route or action demonstrates it
- what prerequisites exist
- what is intentionally out of OSS scope

## Enterprise Boundary Rules

The demo host must never fake Enterprise features in OSS.

If autoscaling, durable spill persistence, reconciliation, or licensing/control-plane features are not active in OSS, the demo should say so directly and explain the boundary.

That honesty is more valuable than a flashy but misleading sample.

## Delivery Plan

1. Define the demo-host project boundary and package references.
2. Build the minimal host shell with health/status pages.
3. Add one verified scenario per active OSS package.
4. Add smoke tests for every scenario.
5. Link the demo host from `README.md`, `docs/INDEX.md`, and `vapecache.net`.

## Definition Of Done

The demo host is ready when:

- a new contributor can run it from the README without guesswork
- each active OSS package has one visible proof path
- test coverage verifies the host is not lying
- the public website can point to it without embarrassment
