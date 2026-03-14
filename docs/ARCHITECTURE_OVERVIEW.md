# Architecture Overview

This document defines the runtime architecture and composition boundaries for the OSS VapeCache codebase.

## 1. Scope

This spec covers:

- package/layer boundaries
- runtime composition paths (Microsoft DI and Autofac)
- request/data flow for caching operations
- where ASP.NET Core and other adapters belong

This spec does not redefine licensing, packaging metadata, or roadmap strategy.

## 2. Layering and Dependency Rules

Canonical dependency rule: dependencies point inward.

- `VapeCache.Core`
  - domain primitives and policy logic
  - no infrastructure/framework dependencies
- `VapeCache.Application`
  - command/query abstractions and invalidation orchestration
  - depends on `Core`
- `VapeCache.Abstractions`
  - public contracts and options (`IVapeCache`, `ICacheService`, connection contracts)
  - independent from `Application` and `Infrastructure`
- `VapeCache.Infrastructure`
  - Redis transport, pool, protocol, hybrid runtime, telemetry implementation
  - implements `Abstractions`
- outer adapters
  - `VapeCache.Extensions.*`, `VapeCache.Console`, `VapeCache.UI`, tests, benchmarks

Enforcement tests: `VapeCache.Tests/Architecture/CleanArchitectureDependencyTests.cs`.

## 3. Runtime Composition

### 3.1 Microsoft DI

Primary entrypoint:

- `services.AddVapeCache()` in `VapeCache.Extensions.DependencyInjection`

This composes:

- `AddVapecacheRedisConnections()` (transport, connection factory, pool)
- `AddVapecacheCaching()` (hybrid cache runtime, stampede protection, codecs, typed client)

Optional capability packages compose on top:

- `VapeCache.Extensions.AspNetCore`
- `VapeCache.Extensions.Aspire`
- `VapeCache.Extensions.PubSub`
- `VapeCache.Extensions.Logging`

### 3.2 Autofac

Autofac modules mirror the same runtime split:

- `VapeCacheConnectionsModule`
- `VapeCacheCachingModule`
- `VapeCachePubSubModule` (optional)

## 4. Runtime Component Graph

```
IVapeCache (typed API)
  -> ICacheService (StampedeProtectedCacheService)
      -> HybridCacheService
          -> RedisCacheService (primary distributed backend)
          -> ICacheFallbackService (in-memory fallback backend)
```

Transport path under `RedisCacheService`:

```
RedisCommandExecutor
  -> RedisMultiplexedConnection lanes
  -> RESP protocol (read/write)
  -> socket/TLS transport
```

## 5. Operation Flows

### 5.1 Get

1. stampede layer forwards read
2. hybrid checks circuit-breaker state
3. if Redis allowed, read Redis first
4. fallback memory used when breaker is open, probe budget is exhausted, or Redis errors
5. if entry is tag-enveloped, versions are validated before returning payload

### 5.2 Set

1. intent/tags from `CacheEntryOptions` are evaluated
2. tagged entries are wrapped with tag-version envelope
3. write goes to Redis when healthy
4. fallback write/mirror behavior depends on `HybridFailoverOptions`
5. reconciliation tracking is used when Redis path is unavailable

### 5.3 Remove

1. fallback remove is always attempted
2. Redis remove is attempted when breaker allows
3. reconciliation delete can be queued when Redis is unavailable

## 6. Invalidation Architecture

Two complementary invalidation paths exist:

- runtime tag/zone versioning in `HybridCacheService` (`ICacheTagService`)
- policy-driven invalidation orchestration in `VapeCache.Features.Invalidation` and `VapeCache.Application.Caching.Invalidation`

Tag/zone versioning is read-time validated and avoids full key scans.

## 7. ASP.NET Core Boundary

ASP.NET Core integration lives in `VapeCache.Extensions.AspNetCore` only.

- output-cache store: `VapeCacheOutputCacheStore`
- policy ergonomics: `VapeCachePolicyAttribute`, `VapeCacheHttpPolicyBuilder`
- middleware/endpoint glue in extension methods

Core runtime packages do not depend on ASP.NET types.

## 8. Pub/Sub Boundary

Redis pub/sub support is optional and isolated:

- contracts: `IRedisPubSubService`, `RedisPubSubOptions`
- implementation: `RedisPubSubService`
- registration package: `VapeCache.Extensions.PubSub`

Pub/sub is not required for core cache GET/SET functionality.

## 9. Observability Boundary

Observability is implemented in infrastructure, exported through standard .NET primitives:

- metrics: `Meter` names `VapeCache.Redis` and `VapeCache.Cache`
- tracing: `ActivitySource` name `VapeCache.Redis`
- logs: `ILogger<T>` in runtime components

Optional sink/platform wiring is externalized in `VapeCache.Extensions.Logging` and host configuration.

## 10. Rules for New Features

- put domain policies in `Core` or `Application`, not in ASP.NET adapter projects
- keep transport/protocol concerns in `Infrastructure`
- expose public capability through `Abstractions` first, then wire adapters
- avoid leaking framework-specific types into cache/runtime contracts
