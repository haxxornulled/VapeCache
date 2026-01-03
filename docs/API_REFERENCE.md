# VapeCache API Reference

This reference covers the public API surface in `VapeCache.Abstractions`.

## Overview

VapeCache exposes three main layers:
- **ICacheService**: byte[] + delegate-based serialization (low-level).
- **IVapeCache**: typed, codec-driven API with `CacheKey<T>` and regions.
- **Typed Collections**: lists/sets/hashes/sorted sets with automatic serialization.

## ICacheService (Low-Level)

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

### Example
```csharp
var bytes = await cache.GetAsync("user:123", ct);

await cache.SetAsync(
    "user:123",
    Encoding.UTF8.GetBytes("payload"),
    new CacheEntryOptions(Ttl: TimeSpan.FromMinutes(5)),
    ct);
```

## IVapeCache (Typed Keys + Codecs)

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

### Example
```csharp
var key = CacheKey<User>.From("users:123");

var user = await vapeCache.GetOrCreateAsync(
    key,
    ct => repository.GetUserAsync("123", ct),
    new CacheEntryOptions(TimeSpan.FromMinutes(10)),
    ct);
```

## Cache Regions

`ICacheRegion` provides key prefixing and typed access:

```csharp
var users = vapeCache.Region("users");
var user = await users.GetOrCreateAsync(
    "123",
    ct => repository.GetUserAsync("123", ct),
    new CacheEntryOptions(TimeSpan.FromMinutes(10)),
    ct);
```

## Typed Collections

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

### Lists
```csharp
var queue = collections.List<WorkItem>("jobs:pending");
await queue.PushBackAsync(item, ct);
var next = await queue.PopFrontAsync(ct);
```

### Sets
```csharp
var online = collections.Set<string>("users:online");
await online.AddAsync("alice", ct);
var all = await online.MembersAsync(ct);
```

### Hashes
```csharp
var profiles = collections.Hash<UserProfile>("users:profiles");
await profiles.SetAsync("alice", profile, ct);
var loaded = await profiles.GetAsync("alice", ct);
```

### Sorted Sets
```csharp
var leaderboard = collections.SortedSet<PlayerScore>("scores:weekly");
await leaderboard.AddAsync(new PlayerScore("alice"), 100, ct);
var top = await leaderboard.RangeByRankAsync(0, 9, descending: true, ct);
```

## JSON + Module Services

When Redis modules are available, the following helpers are registered:
- `IJsonCache` (JSON.GET/JSON.SET/JSON.DEL)
- `IRedisSearchService` (RediSearch)
- `IRedisBloomService` (RedisBloom)
- `IRedisTimeSeriesService` (RedisTimeSeries)

Module detection is exposed via `IRedisModuleDetector`.

### Zero-copy JSON (lease)

For high-throughput JSON workloads, use the lease-based APIs on `IJsonCache` to avoid extra byte[] allocations. Always dispose the lease.

```csharp
var jsonCache = serviceProvider.GetRequiredService<IJsonCache>();

using var lease = await jsonCache.GetLeaseAsync("doc:1", ".", ct);
if (!lease.IsNull)
{
    // Use lease.Span/Memory before disposing.
    await jsonCache.SetLeaseAsync("doc:copy:1", lease, ".", ct);
}
```

## CacheEntryOptions

```csharp
public readonly record struct CacheEntryOptions(TimeSpan? Ttl = null);
```

## Error Handling

- Redis/network failures surface as exceptions from the Redis executor.
- The hybrid cache (`HybridCacheService` + `HybridCommandExecutor`) catches Redis failures and fails over to the configured fallback for core cache operations, including module commands.
- Module fallback semantics are simplified; see `docs/REDIS_MODULES.md`.

## Related Docs
- [CONFIGURATION.md](CONFIGURATION.md)
- [REDIS_PROTOCOL_SUPPORT.md](REDIS_PROTOCOL_SUPPORT.md)
- [TYPED_COLLECTIONS.md](TYPED_COLLECTIONS.md)
