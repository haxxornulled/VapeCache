# VapeCache And FusionCache

This document explains the practical positioning between VapeCache and FusionCache for teams evaluating both.

## Short Version

VapeCache can cover a meaningful part of the same problem space as FusionCache, but the native product shape is different.

- FusionCache is primarily an application-level cache orchestration library.
- VapeCache is primarily a Redis-first runtime platform with cache APIs and framework integrations.
- VapeCache can also act as a distributed L2 behind other cache abstractions through `IDistributedCache` / `IBufferDistributedCache`.

The important message for VapeCache docs is not "we mimic FusionCache."
It is "we solve the Redis runtime path directly, and we can also interoperate with higher-level cache abstractions when that helps adoption."

## What VapeCache Native Hybrid Caching Means

When we say VapeCache supports hybrid caching, we mean the native runtime combines:

- Redis as the primary backend
- node-local in-memory fallback when Redis is degraded or unavailable
- circuit-breaker state with manual failover controls
- stampede protection around hot keys and cache-aside factories
- fallback warming and write mirroring while Redis is healthy
- tag and zone version invalidation
- typed APIs through `IVapeCache`
- ASP.NET Core output-cache storage integration
- OpenTelemetry metrics and tracing across the runtime path

This is not just an adapter over a generic distributed-cache provider.
It is the first-class execution model of the native runtime.

See [HYBRID_CACHING_API_SURFACE.md](HYBRID_CACHING_API_SURFACE.md) for the concrete contract.

## Where VapeCache Is Purpose-Built

Use VapeCache as the lead story when you care about:

- Redis being a first-class subsystem in the architecture
- transport/runtime behavior instead of only call-site cache semantics
- outage tolerance with circuit-breaker plus memory fallback
- ASP.NET Core output-cache integration
- Aspire integration and operational observability
- Redis-adjacent packages such as pub/sub, streams, EF Core support, invalidation, and RediSearch projections
- avoiding a production runtime dependency on `StackExchange.Redis`

That last point is explicit in this repository: the production runtime packages do not reference `StackExchange.Redis`.
Benchmark and console projects reference it for comparison and migration work, but the runtime packages do not.

## Where FusionCache Interop Fits

The VapeCache distributed-cache bridge exists for teams that already have:

- `IDistributedCache` in their app architecture
- ASP.NET Core components built around the Microsoft abstraction
- FusionCache configured to use the host's registered distributed-cache service as L2

That gives a clean migration and interop story:

1. keep the current application-facing abstraction
2. route the distributed cache layer through VapeCache
3. move to native VapeCache APIs later if you want the fuller runtime surface

Recommended interop framing:

- "FusionCache L2 over VapeCache is supported"
- "The distributed-cache adapter is a compatibility bridge"
- "Native VapeCache remains the recommended path for the full runtime model"

See [DISTRIBUTED_CACHE_BRIDGE.md](DISTRIBUTED_CACHE_BRIDGE.md).

For a candid feature-gap view, see [FUSIONCACHE_GAP_ANALYSIS.md](FUSIONCACHE_GAP_ANALYSIS.md).

## Messaging Guidance

Preferred:

- "VapeCache is a Redis-first caching runtime, not just a generic cache adapter."
- "VapeCache hybrid caching means Redis primary plus in-memory failover, stampede control, invalidation, and operational runtime controls."
- "VapeCache can replace the distributed backend under existing `IDistributedCache` or FusionCache L2 setups."
- "VapeCache runtime packages do not depend on `StackExchange.Redis`."

Avoid:

- claiming 1:1 feature equivalence where the docs do not prove it
- implying the `IDistributedCache` adapter is the primary VapeCache experience
- making unsupported head-to-head performance claims against FusionCache

## Recommended Architectural Read

- If your top priority is polished call-site cache orchestration, FusionCache is a natural comparison point.
- If your top priority is Redis transport, failover behavior, output caching, observability, and runtime control, VapeCache is the Redis-first choice.
- If you want both, a sensible migration topology is FusionCache on top with VapeCache behind the distributed L2 boundary, then a later move to native VapeCache APIs where appropriate.
