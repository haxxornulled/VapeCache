# Upgrade Notes

These are the release-critical behavior changes to account for before shipping or upgrading hosts.

## New Distributed Cache Bridge

`VapeCache.Extensions.DistributedCache` adds a compatibility bridge for `IDistributedCache` / `IBufferDistributedCache`.

Intended use:

- migration from another distributed-cache provider
- framework/middleware compatibility paths
- FusionCache-style distributed L2 interoperability

Recommended guidance:

- treat it as an interoperability layer, not the preferred headline experience
- keep the existing cache abstraction if that makes adoption easier, then migrate to native VapeCache later when you want the fuller runtime model
- use a dedicated key prefix when sharing the same backend with native VapeCache consumers
- market it as "route your distributed cache layer through VapeCache with minimal code changes"

## New KeyDB Package Boundary

`VapeCache.Extensions.KeyDB` provides an explicit KeyDB registration surface while reusing the same proven Redis-protocol runtime.

What changed:

- new package: `VapeCache.Extensions.KeyDB`
- new DI entry point: `AddVapeCacheKeyDb(...)`
- default connection section for this path: `KeyDbConnection`
- connection-string parser now accepts `keydb://` and `keydbs://` aliases in addition to `redis://` and `rediss://`

Migration guidance:

- if your current Redis-targeted hosts already pass compatibility validation against KeyDB, runtime behavior should remain unchanged
- use the KeyDB package when you want explicit backend intent and clearer configuration ownership
- keep compatibility proof in CI with the KeyDB integration harness script:

```powershell
.\infra\scripts\run-keydb-compat.ps1 -Configuration Release -TestFilter "FullyQualifiedName~VapeCache.Tests.Integration"
```

## Required Startup Binding

If you register the core services directly:

```csharp
builder.Services.AddOptions<RedisConnectionOptions>()
    .Bind(builder.Configuration.GetSection("RedisConnection"));

builder.Services.AddVapeCacheRedisConnections();
builder.Services.AddVapeCacheCaching();
```

VapeCache now validates `RedisConnectionOptions` and `RedisMultiplexerOptions` at startup. A host with no Redis endpoint configured fails fast instead of waiting for first traffic.

If you prefer environment variables, set:

```bash
VAPECACHE_REDIS_CONNECTIONSTRING=redis://localhost:6379/0
```

## Security Defaults

- `RedisConnectionOptions.AllowAuthFallbackToPasswordOnly` now defaults to `false`.
- Enterprise online revocation checks fail closed by default once `VAPECACHE_LICENSE_REVOCATION_ENABLED=true`.

Only opt back into fail-open behavior when you have explicitly accepted that risk:

```bash
VAPECACHE_LICENSE_REVOCATION_FAIL_OPEN=true
```

## Aspire Endpoint Mapping

`WithAutoMappedEndpoints(...)` now registers the startup filter only. The wrapper endpoints are not mapped unless you explicitly enable them:

```csharp
builder.AddVapeCache()
    .WithAutoMappedEndpoints(options =>
    {
        options.Enabled = true;
    });
```

This keeps diagnostics and admin surfaces disabled by default.

## Metric Contract

`cache.current.backend` now reports:

- `1` = Redis
- `0` = in-memory fallback
- `-1` = unknown / not initialized

## Allocation Profile Update

Recent hot-path allocation work was validated with baseline/current captures.

- Output-cache store path (`VapeCacheOutputCacheStore`): `3940.00` -> `3772.00 bytes/call` (`-4.26%`).
- Redis set/get hot path (`IRedisCommandExecutor`): `3777.87` -> `3604.11 bytes/call` (`-4.60%`).
- Redis map-response hotspot pair (map-get/map-set await paths): `~299.45` -> `~82.01 sampled bytes/call` combined (`~72.6%` reduction).
