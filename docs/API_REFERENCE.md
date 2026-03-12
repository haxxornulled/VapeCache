# VapeCache API Reference

This is the source of truth for the public API surface:
- `VapeCache.Abstractions`
- `VapeCache.Infrastructure` fluent options extensions
- `VapeCache.Extensions.Aspire` host/wrapper integrations

Use this page for exact signatures and endpoint contracts.

For runtime behavior guarantees and operational semantics across breaker/failover states, see [HYBRID_CACHING_API_SURFACE.md](HYBRID_CACHING_API_SURFACE.md).

## Core Cache APIs

### `ICacheService` (low-level bytes + delegates)

Namespace: `VapeCache.Abstractions.Caching`

```csharp
public interface ICacheService
{
    string Name { get; }

    ValueTask<byte[]?> GetAsync(string key, CancellationToken ct);
    ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CacheEntryOptions options, CancellationToken ct);
    ValueTask<bool> RemoveAsync(string key, CancellationToken ct);

    ValueTask<T?> GetAsync<T>(string key, SpanDeserializer<T> deserialize, CancellationToken ct);
    ValueTask SetAsync<T>(string key, T value, Action<IBufferWriter<byte>, T> serialize, CacheEntryOptions options, CancellationToken ct);

    ValueTask<T> GetOrSetAsync<T>(
        string key,
        Func<CancellationToken, ValueTask<T>> factory,
        Action<IBufferWriter<byte>, T> serialize,
        SpanDeserializer<T> deserialize,
        CacheEntryOptions options,
        CancellationToken ct);
}
```

### `IVapeCache` (typed ergonomic API)

Namespace: `VapeCache.Abstractions.Caching`

```csharp
public interface IVapeCache
{
    ICacheRegion Region(string name);
    ValueTask<T?> GetAsync<T>(CacheKey<T> key, CancellationToken ct = default);
    ValueTask SetAsync<T>(CacheKey<T> key, T value, CacheEntryOptions options = default, CancellationToken ct = default);
    ValueTask<T> GetOrCreateAsync<T>(
        CacheKey<T> key,
        Func<CancellationToken, ValueTask<T>> factory,
        CacheEntryOptions options = default,
        CancellationToken ct = default);
    ValueTask<bool> RemoveAsync(CacheKey key, CancellationToken ct = default);
    ValueTask<long> InvalidateTagAsync(string tag, CancellationToken ct = default);
    ValueTask<long> GetTagVersionAsync(string tag, CancellationToken ct = default);
    ValueTask<long> InvalidateZoneAsync(string zone, CancellationToken ct = default);
    ValueTask<long> GetZoneVersionAsync(string zone, CancellationToken ct = default);
}
```

### `ICacheTagService` (tag/zone invalidation)

Namespace: `VapeCache.Abstractions.Caching`

```csharp
public interface ICacheTagService
{
    ValueTask<long> InvalidateTagAsync(string tag, CancellationToken ct = default);
    ValueTask<long> GetTagVersionAsync(string tag, CancellationToken ct = default);
    ValueTask<long> InvalidateZoneAsync(string zone, CancellationToken ct = default);
    ValueTask<long> GetZoneVersionAsync(string zone, CancellationToken ct = default);
}
```

### `ICacheChunkStreamService` (large payload streaming)

Namespace: `VapeCache.Abstractions.Caching`

```csharp
public interface ICacheChunkStreamService
{
    ValueTask<CacheChunkStreamManifest> WriteAsync(
        string key,
        Stream source,
        CacheEntryOptions options = default,
        CacheChunkStreamWriteOptions? writeOptions = null,
        CancellationToken ct = default);

    ValueTask<CacheChunkStreamManifest?> GetManifestAsync(string key, CancellationToken ct = default);
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(string key, CancellationToken ct = default);
    ValueTask<bool> CopyToAsync(string key, Stream destination, CancellationToken ct = default);
    ValueTask<bool> RemoveAsync(string key, CancellationToken ct = default);
}
```

`WriteAsync` stores stream content as chunked cache entries plus a manifest.  
Because it uses the active `ICacheService`, hybrid deployments automatically fail over to in-memory when Redis is unavailable.

### `ICacheRegion`

Namespace: `VapeCache.Abstractions.Caching`

```csharp
public interface ICacheRegion
{
    string Name { get; }
    CacheKey<T> Key<T>(string id);
    ValueTask<T> GetOrCreateAsync<T>(string id, Func<CancellationToken, ValueTask<T>> factory, CacheEntryOptions options = default, CancellationToken ct = default);
    ValueTask<T?> GetAsync<T>(string id, CancellationToken ct = default);
    ValueTask SetAsync<T>(string id, T value, CacheEntryOptions options = default, CancellationToken ct = default);
    ValueTask<bool> RemoveAsync(string id, CancellationToken ct = default);
}
```

## Entry Options and Intent-Aware Caching

### `CacheEntryOptions`

```csharp
public readonly record struct CacheEntryOptions(
    TimeSpan? Ttl = null,
    CacheIntent? Intent = null);
```

Tag and zone helpers:

- `CacheEntryOptions.WithTag(string tag)`
- `CacheEntryOptions.WithTags(params string[] tags)`
- `CacheEntryOptions.WithZone(string zone)`
- `CacheEntryOptions.WithZones(params string[] zones)`

### `CacheIntent`

```csharp
public sealed record CacheIntent(
    CacheIntentKind Kind,
    string? Reason = null,
    string? Owner = null,
    string[]? Tags = null);
```

Intent kinds:
- `Unspecified`
- `ReadThrough`
- `QueryResult`
- `SessionState`
- `Idempotency`
- `RateLimit`
- `FeatureFlag`
- `ComputedView`
- `Preload`

### `ICacheIntentRegistry`

Used by wrapper endpoints and diagnostics to explain why entries exist.

```csharp
public interface ICacheIntentRegistry
{
    void RecordSet(string key, string backend, in CacheEntryOptions options, int payloadBytes);
    void RecordRemove(string key);
    bool TryGet(string key, out CacheIntentEntry? entry);
    IReadOnlyList<CacheIntentEntry> GetRecent(int maxCount);
}
```

## Stampede Protection APIs

### `CacheStampedeOptions`

Namespace: `VapeCache.Abstractions.Caching`

```csharp
public sealed record CacheStampedeOptions
{
    bool Enabled { get; set; }
    int MaxKeys { get; set; }
    bool RejectSuspiciousKeys { get; set; }
    int MaxKeyLength { get; set; }
    TimeSpan LockWaitTimeout { get; set; }
    bool EnableFailureBackoff { get; set; }
    TimeSpan FailureBackoff { get; set; }
}
```

### `HybridFailoverOptions`

```csharp
public sealed class HybridFailoverOptions
{
    bool MirrorWritesToFallbackWhenRedisHealthy { get; set; }
    bool WarmFallbackOnRedisReadHit { get; set; }
    TimeSpan FallbackWarmReadTtl { get; set; }
    TimeSpan FallbackMirrorWriteTtlWhenMissing { get; set; }
    int MaxMirrorPayloadBytes { get; set; }
    bool RemoveStaleFallbackOnRedisMiss { get; set; }
}
```

## Redis Transport Options

### `RedisConnectionOptions` (cluster + protocol knobs)

Namespace: `VapeCache.Abstractions.Connections`

```csharp
public sealed record RedisConnectionOptions
{
    int RespProtocolVersion { get; init; } // 2 or 3
    bool EnableClusterRedirection { get; init; } // MOVED/ASK handling for cache-path commands
    int MaxClusterRedirects { get; init; } // 0..16
}
```

Key behavior:
- `RespProtocolVersion=3` enables HELLO 3 negotiation. If negotiation fails, handshake falls back safely.
- `EnableClusterRedirection=true` enables bounded MOVED/ASK retries.
- `MaxClusterRedirects` limits redirect hops for one command to prevent loops.

### `IRedisConnectionStringBuilder`

Namespace: `VapeCache.Abstractions.Connections`

```csharp
public interface IRedisConnectionStringBuilder
{
    string Build(RedisConnectionOptions options);
}
```

Use this builder instead of manual string concatenation when creating `redis://`/`rediss://` URIs.
It enforces host-only input, TLS-option consistency, and safe IPv6 authority formatting.

### Named profiles

```csharp
public enum CacheStampedeProfile
{
    Strict,
    Balanced,
    Relaxed
}
```

### Fluent options extensions

Namespace: `VapeCache.Infrastructure.Caching`

```csharp
services.AddOptions<CacheStampedeOptions>()
    .UseCacheStampedeProfile(CacheStampedeProfile.Balanced)
    .ConfigureCacheStampede(options =>
    {
        options.WithLockWaitTimeout(TimeSpan.FromMilliseconds(600))
            .WithFailureBackoff(TimeSpan.FromMilliseconds(400));
    })
    .Bind(configuration.GetSection("CacheStampede"));
```

## Cache Statistics APIs

### `ICacheStats`

```csharp
public interface ICacheStats
{
    CacheStatsSnapshot Snapshot { get; }
}
```

### `CacheStatsSnapshot`

```csharp
public readonly record struct CacheStatsSnapshot(
    long GetCalls,
    long Hits,
    long Misses,
    long SetCalls,
    long RemoveCalls,
    long FallbackToMemory,
    long RedisBreakerOpened,
    long StampedeKeyRejected,
    long StampedeLockWaitTimeout,
    long StampedeFailureBackoffRejected);
```

## Typed Collection APIs

Namespace: `VapeCache.Abstractions.Collections`

```csharp
public interface ICacheCollectionFactory
{
    ICacheList<T> List<T>(string key);
    ICacheSet<T> Set<T>(string key);
    ICacheHash<T> Hash<T>(string key);
    ICacheSortedSet<T> SortedSet<T>(string key);
}
```

Examples:

```csharp
var jobs = collections.List<string>("jobs:pending");
await jobs.PushBackAsync("job-1", ct);
var next = await jobs.PopFrontAsync(ct);

var online = collections.Set<string>("users:online");
await online.AddAsync("alice", ct);

var profiles = collections.Hash<UserProfile>("users:profiles");
await profiles.SetAsync("alice", profile, ct);

var scores = collections.SortedSet<string>("scores:weekly");
await scores.AddAsync("alice", 100, ct);
```

## JSON and Redis Module APIs

Interfaces:
- `IJsonCache`
- `IRedisSearchService`
- `IRedisBloomService`
- `IRedisTimeSeriesService`
- `IRedisModuleDetector`

High-throughput JSON path (lease-based):

```csharp
using var lease = await jsonCache.GetLeaseAsync("doc:1", ".", ct);
if (!lease.IsNull)
{
    await jsonCache.SetLeaseAsync("doc:copy:1", lease, ".", ct);
}
```

`RedisValueLease` is a sealed reference type and must be disposed when pooled.

## Aspire Wrapper APIs

### Host builder extensions

Namespace: `VapeCache.Extensions.Aspire`

```csharp
builder.AddVapeCache()
    .WithRedisFromAspire("redis")
    .WithHealthChecks()
    .WithAspireTelemetry()
    .WithCacheStampedeProfile(CacheStampedeProfile.Balanced)
    .WithAutoMappedEndpoints(options =>
    {
        options.Enabled = true;
    });
```

### Endpoint mapping

```csharp
app.MapVapeCacheEndpoints(
    prefix: "/vapecache",
    includeBreakerControlEndpoints: false,
    includeLiveStreamEndpoint: true,
    includeIntentEndpoints: true,
    includeDashboardEndpoint: true);

app.MapVapeCacheAdminEndpoints(
    prefix: "/internal/vapecache-admin",
    requireAuthorization: true,
    authorizationPolicy: "VapeCacheAdmin");
```

Default wrapper routes:
- `GET /vapecache/status`
- `GET /vapecache/stats`
- `GET /vapecache/stream`
- `GET /vapecache/dashboard`
- `GET /vapecache/intent/{key}`
- `GET /vapecache/intent?take=50`

Admin-only control routes (map on a separate internal prefix):
- `POST /vapecache/admin/breaker/force-open` (opt-in)
- `POST /vapecache/admin/breaker/clear` (opt-in)

Use `requireAuthorization: true` (or a named `authorizationPolicy`) to enforce auth on the admin control group in one call.

Status/stats payloads include spill diagnostics:
- `spill.mode` (`noop` or `file`)
- `spill.totalSpillFiles`
- `spill.activeShards`
- `spill.maxFilesInShard`
- `spill.imbalanceRatio`
- `spill.topShards`

## ASP.NET Core Pipeline Hooks

Namespace: `VapeCache.Extensions.AspNetCore`

```csharp
services.AddVapeCacheOutputCaching(
    configureOutputCache: options =>
    {
        options.AddBasePolicy(policy => policy.Expire(TimeSpan.FromSeconds(30)));
    },
    configureStore: store =>
    {
        store.KeyPrefix = "vapecache:output";
        store.DefaultTtl = TimeSpan.FromSeconds(30);
        store.EnableTagIndexing = true;
    });

services.AddVapeCacheAspNetPolicies(policies =>
{
    policies.AddPolicy("products", policy => policy
        .Ttl(TimeSpan.FromMinutes(5))
        .VaryByQuery()
        .VaryByHeaders("x-tenant-id")
        .Tags("products", "catalog"));
});
```

```csharp
app.UseVapeCacheOutputCaching();
```

Minimal API endpoint hook:

```csharp
app.MapGet("/products/{id:int}", (int id) => Results.Ok(new { id }))
   .CacheWithVapeCache();

app.MapGet("/search", (string q) => Results.Ok(new { q }))
   .CacheWithVapeCache(policy => policy
       .Ttl(TimeSpan.FromSeconds(60))
       .VaryByQuery()
       .Tags("search"));

app.MapGet("/products/{id:int}", (int id) => Results.Ok(new { id }))
   .CacheWithVapeCache("products");
```

MVC/controller attribute hook:

```csharp
[VapeCachePolicy("products", TtlSeconds = 300, VaryByQuery = true, CacheTags = new[] { "products" })]
public IActionResult GetProduct(int id) => Ok(new { id });
```

Store options:

```csharp
public sealed class VapeCacheOutputCacheStoreOptions
{
    string KeyPrefix { get; set; } = "vapecache:output";
    TimeSpan DefaultTtl { get; set; } = TimeSpan.FromSeconds(30);
    bool EnableTagIndexing { get; set; } = true;
}
```

Sticky-session affinity hints for clustered failover:

```csharp
services.AddVapeCacheFailoverAffinityHints(options =>
{
    options.NodeId = "node-1";
    options.CookieName = "VapeCacheAffinity";
});

app.UseVapeCacheFailoverAffinityHints();
```

## Autoscaler Diagnostics API

Namespace: `VapeCache.Abstractions.Connections`

```csharp
public interface IRedisMultiplexerDiagnostics
{
    RedisAutoscalerSnapshot GetAutoscalerSnapshot();
    IReadOnlyList<RedisMuxLaneSnapshot> GetMuxLaneSnapshots();
}
```

`RedisAutoscalerSnapshot` includes current/target connection counts, queue and inflight pressure, rolling p95/p99, freeze state, and last scale decision metadata.

`RedisMuxLaneSnapshot` exposes per-lane transport counters and queue pressure for Aspire/dashboard graphing (`laneIndex`, `connectionId`, `role`, `writeQueueDepth`, `inFlight`, `inFlightUtilization`, `bytesSent`, `bytesReceived`, `operations`, `failures`, `responses`, `orphanedResponses`, `responseSequenceMismatches`, `transportResets`, `healthy`).

## Spill Diagnostics API

Namespace: `VapeCache.Abstractions.Caching`

```csharp
public interface ISpillStoreDiagnostics
{
    SpillStoreDiagnosticsSnapshot GetSnapshot();
}
```

`SpillStoreDiagnosticsSnapshot` exposes spill mode (`noop`/`file`) and shard-balance signals (`activeShards`, `maxFilesInShard`, `imbalanceRatio`, `topShards`) for live scatter verification.

## Error Handling Behavior

- Redis/network failures can propagate from the Redis executor.
- Hybrid cache mode can fail over to in-memory behavior based on breaker state.
- Stampede lock-wait timeout surfaces as `TimeoutException`.
- Wrapper breaker control endpoints should be mapped on an internal admin prefix with authN/authZ when enabled.

Bottom line: autoscaling, wrapper endpoints, and extra diagnostics are operational layers. Core cache correctness does not depend on them.

## Related Docs

- [QUICKSTART.md](QUICKSTART.md)
- [CONFIGURATION.md](CONFIGURATION.md)
- [ASPIRE_INTEGRATION.md](ASPIRE_INTEGRATION.md)
- [TYPED_COLLECTIONS.md](TYPED_COLLECTIONS.md)
- [REDIS_MODULES.md](REDIS_MODULES.md)
- [ENTERPRISE_MULTIPLEXER_AUTOSCALER.md](ENTERPRISE_MULTIPLEXER_AUTOSCALER.md)
