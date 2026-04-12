# FusionCache Gap Analysis

This document compares the current VapeCache OSS runtime surface against the publicly documented FusionCache feature set.

Snapshot date: April 6, 2026.

Intent:

- document what FusionCache publicly advertises today
- identify what VapeCache does not currently expose, or does only partially
- highlight the gaps most worth considering for VapeCache

This is a product/architecture analysis, not a benchmark claim.
It should not be used to claim 1:1 parity or performance superiority without dedicated evidence.

## Source Basis

FusionCache features were compared against its public GitHub materials:

- FusionCache README feature list: <https://github.com/ZiggyCreatures/FusionCache>
- FusionCache release notes for newer features like `Clear()` and `HybridCache` support: <https://github.com/ZiggyCreatures/FusionCache/releases>

VapeCache features were compared against this repository's public code and docs, especially:

- [README.md](../README.md)
- [HYBRID_CACHING_API_SURFACE.md](HYBRID_CACHING_API_SURFACE.md)
- [DISTRIBUTED_CACHE_BRIDGE.md](DISTRIBUTED_CACHE_BRIDGE.md)
- [API_REFERENCE.md](API_REFERENCE.md)
- [NON_GOALS.md](NON_GOALS.md)

## Summary

VapeCache already overlaps with FusionCache on some important ground:

- typed cache-aside API
- hybrid Redis-primary plus memory-fallback runtime
- stampede protection
- tagging and zone invalidation
- OpenTelemetry and logging
- `IDistributedCache` interoperability path

The main public gaps versus FusionCache today are not basic caching, but higher-level cache semantics and orchestration features:

- fail-safe stale reuse
- soft/hard timeout orchestration
- eager refresh and background refresh semantics
- conditional refresh
- generic L1+L2 portability across arbitrary `IDistributedCache` providers
- explicit named caches
- full-cache clear semantics
- Microsoft `HybridCache` implementation/adapter
- auto-clone / defensive copy support
- synchronous API parity

## Feature Matrix

| FusionCache feature | VapeCache status | Notes |
|---|---|---|
| Cache stampede protection | Present | Native in VapeCache via stampede controls and single-flight behavior. |
| Hybrid cache runtime | Present, different shape | VapeCache native model is Redis primary plus memory fallback, not a generic memory-plus-any-`IDistributedCache` abstraction. |
| L1+L2 over any `IDistributedCache` | Missing as native story | VapeCache is intentionally Redis-first. Interop exists through the distributed-cache bridge, but that is not the native runtime model. |
| Backplane sync across nodes | Not documented as native cache feature | VapeCache has pub/sub packages and invalidation packages, but not a clearly documented FusionCache-style cache backplane story for node sync. |
| Fail-safe reuse of expired entries | Missing | VapeCache fallback helps during Redis outages, but that is different from serving an expired cached value as a temporary resilience policy during factory or distributed-cache failure. |
| Soft/hard timeouts | Missing | No public native per-entry timeout orchestration model equivalent to FusionCache soft/hard timeout semantics. |
| Eager refresh | Missing | No documented native pre-expiration background refresh behavior. |
| Conditional refresh | Missing | No documented equivalent to validator/conditional refresh semantics. |
| Background distributed operations | Missing/not documented | VapeCache does background-oriented runtime work internally, but not a documented public cache semantic matching FusionCache's feature. |
| Named caches | Present, partial | VapeCache now supports named cache scopes plus logical clear. It does not yet expose the broader per-cache configuration/orchestration surface FusionCache documents. |
| Tagging | Present | VapeCache supports tags and zones through version invalidation. |
| Clear entire cache | Present, logical clear | VapeCache now exposes `ClearAsync()` on `IVapeCache` scopes and `RemoveByTagAsync("*")` on the `HybridCache` adapter. This is logical invalidation, not destructive backend key scanning. |
| Microsoft `HybridCache` implementation | Present, partial | VapeCache now exposes a native `HybridCache` adapter over `IVapeCache`. Core API support is present, but option-flag semantics are not a byte-for-byte match for every Microsoft implementation detail. |
| Adaptive caching | Missing | `CacheEntryOptions` is fixed up front; no documented "derive entry options from the produced value" model. |
| Auto-clone | Missing | No documented defensive cloning of returned cached values. |
| Sync + async API parity | Missing | VapeCache public app-facing API is async-first. |
| Plugin model | Missing/not documented | VapeCache has extension packages, but not a documented plugin/event extensibility model analogous to FusionCache's plugin language. |
| Events | Partial/not documented | OTEL/logging are present, but a public event surface comparable to FusionCache events is not clearly documented. |
| Circuit-breaker around distributed dependencies | Present | VapeCache has a stronger Redis runtime and breaker/failover story than a generic app cache library. |
| OpenTelemetry | Present | Native OTEL support exists. |
| Logging | Present | Native logging exists. |
| Null caching | Unclear | Not prominently documented as a first-class semantic. Avoid parity claims until documented/tested explicitly. |
| Dynamic jitter | Missing/not documented | No prominent public entry-level jitter option is documented today. |
| Auto-recovery | Partial, different shape | VapeCache has reconnect/reconciliation/failover behaviors, but not the same documented cache-wide auto-recovery story. |

## Important Distinctions

Some features may sound similar while solving different problems.

### Fail-safe is not the same as memory fallback

FusionCache fail-safe means an expired value may still be reused temporarily when a refresh path fails.

VapeCache memory fallback means:

- Redis is the primary backend
- node-local memory can continue serving traffic when Redis is unavailable
- mirrored/warmed fallback state helps continuity during outage windows

Those are both resiliency features, but they are not the same behavior.

### Interop is not the same as native parity

VapeCache can sit under FusionCache through `IDistributedCache`.
That is useful and valid.

But it does not mean VapeCache natively exposes every FusionCache semantic at the application call site.

### Redis-first is a strategic constraint

FusionCache is intentionally broad across memory and generic distributed-cache providers.
VapeCache is intentionally opinionated around Redis as a first-class subsystem.

That means some FusionCache features should be copied, and some should not.

## What VapeCache Probably Should Add

These are the highest-value FusionCache-style gaps for VapeCache to consider.

### 1. Fail-safe stale reuse

Why it matters:

- this is one of FusionCache's most compelling application-facing resilience features
- it closes an important gap between "Redis outage handling" and "origin/factory failure handling"
- it makes cache-aside reads more robust even when Redis itself is healthy

Suggested VapeCache framing:

- support stale-while-error behavior on native `GetOrCreateAsync`
- make it explicit and opt-in per entry or per profile
- integrate it cleanly with tags/zones and stampede logic

### 2. Soft and hard timeouts

Why it matters:

- these are strong latency-control features for real APIs
- they help protect callers from slow origins or slow distributed paths
- they would complement VapeCache's runtime-control story well

Suggested VapeCache framing:

- soft timeout: return cached or stale data while refresh continues
- hard timeout: bound total wait time for a cache refresh path

### 3. Eager refresh

Why it matters:

- prevents hot entries from falling off a cliff at expiration
- pairs naturally with Redis-first hot-key workloads
- reduces p99 spikes around expiration boundaries

Suggested VapeCache framing:

- background refresh window before TTL expiry
- safe single-flight interaction with stampede protection
- optional only for hot/read-heavy paths

### 4. Full-cache clear semantics

Why it matters:

- operationally useful for incident response, maintenance, and admin tooling
- expected by many cache users evaluating parity

Suggested VapeCache framing:

- version-based logical clear, not necessarily destructive physical key scanning
- make it work with tags/zones/prefixes and output-cache store boundaries

### 5. Microsoft `HybridCache` integration

Status:

- implemented as a native VapeCache-backed adapter
- documented in [MICROSOFT_HYBRIDCACHE.md](MICROSOFT_HYBRIDCACHE.md)

Remaining opportunity:

- broaden `HybridCacheEntryOptions.Flags` fidelity where it makes sense
- document exact behavioral deltas versus the Microsoft reference shape

### 6. Named caches

Why it matters:

- useful for large apps with different cache profiles and ownership boundaries
- easier mental model than only regions plus global settings

Suggested VapeCache framing:

- support multiple logical cache instances with different defaults
- keep regions as a key-namespace tool, separate from named runtime configurations

## What VapeCache Might Add Later

These are valuable, but likely lower priority than the items above.

- conditional refresh
- adaptive caching
- auto-clone
- sync API parity
- richer public events surface
- explicit dynamic jitter settings

## What VapeCache Probably Should Not Copy Blindly

Not every FusionCache feature is automatically right for VapeCache.

- generic provider portability: this weakens the Redis-first value proposition if it becomes the headline
- full semantic parity through `IDistributedCache`: the abstraction is too narrow to express the full runtime cleanly
- feature sprawl that blurs the line between runtime platform and every possible app-cache convenience

The best strategy is likely:

- copy the highest-value resilience and orchestration semantics
- keep the Redis-first runtime identity
- use interop layers for migration, not as the primary design center

## Performance Claims Policy For This Comparison

VapeCache may deliver high efficiency in some Redis-centric scenarios.
But this repository does not currently contain a public, apples-to-apples VapeCache-versus-FusionCache benchmark series.

Until that exists, do not document:

- "VapeCache is faster than FusionCache"
- "VapeCache beats FusionCache"

Preferred wording today:

- "VapeCache is optimized for Redis transport and runtime efficiency"
- "VapeCache includes benchmark evidence against StackExchange.Redis-oriented baselines"
- "FusionCache head-to-head performance claims require dedicated comparative benchmarks"

## Recommended Next Docs

To make this useful externally, pair this document with:

1. a future roadmap page that labels each gap as `planned`, `considering`, `not planned`, or `intentionally different`
2. a feature-by-feature implementation checklist for the top six opportunities
3. a dedicated benchmark plan before making public speed claims against FusionCache
