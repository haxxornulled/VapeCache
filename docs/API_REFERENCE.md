# VapeCache API Reference

This is the source of truth for the public API surface:
- `VapeCache.Abstractions`
- `VapeCache.Infrastructure` fluent options extensions
- `VapeCache.Extensions.Aspire` host/wrapper integrations

Use this page for exact signatures and endpoint contracts.

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
}
```

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
    .WithAutoMappedEndpoints();
```

### Endpoint mapping

```csharp
app.MapVapeCacheEndpoints(
    prefix: "/vapecache",
    includeBreakerControlEndpoints: false,
    includeLiveStreamEndpoint: true,
    includeIntentEndpoints: true);
```

Default wrapper routes:
- `GET /vapecache/status`
- `GET /vapecache/stats`
- `GET /vapecache/stream`
- `GET /vapecache/intent/{key}`
- `GET /vapecache/intent?take=50`
- `POST /vapecache/breaker/force-open` (opt-in)
- `POST /vapecache/breaker/clear` (opt-in)

## Autoscaler Diagnostics API

Namespace: `VapeCache.Abstractions.Connections`

```csharp
public interface IRedisMultiplexerDiagnostics
{
    RedisAutoscalerSnapshot GetAutoscalerSnapshot();
}
```

`RedisAutoscalerSnapshot` includes current/target connection counts, queue and inflight pressure, rolling p95/p99, freeze state, and last scale decision metadata.

## Error Handling Behavior

- Redis/network failures can propagate from the Redis executor.
- Hybrid cache mode can fail over to in-memory behavior based on breaker state.
- Stampede lock-wait timeout surfaces as `TimeoutException`.
- Wrapper breaker control endpoints should be protected with authN/authZ when enabled.

Bottom line: autoscaling, wrapper endpoints, and extra diagnostics are operational layers. Core cache correctness does not depend on them.

## Related Docs

- [QUICKSTART.md](QUICKSTART.md)
- [CONFIGURATION.md](CONFIGURATION.md)
- [ASPIRE_INTEGRATION.md](ASPIRE_INTEGRATION.md)
- [TYPED_COLLECTIONS.md](TYPED_COLLECTIONS.md)
- [REDIS_MODULES.md](REDIS_MODULES.md)
- [ENTERPRISE_MULTIPLEXER_AUTOSCALER.md](ENTERPRISE_MULTIPLEXER_AUTOSCALER.md)
