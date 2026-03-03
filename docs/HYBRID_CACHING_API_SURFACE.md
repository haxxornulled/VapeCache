# Hybrid Caching API Surface

This document defines the production API surface for VapeCache hybrid caching (Redis primary + in-memory failover).

It is the integration contract for application developers and platform teams.

## Scope

- Primary read/write API for application code.
- Tag and zone invalidation APIs.
- Operational control/state APIs for outage drills and observability.
- Option knobs that shape hybrid behavior.

## Registration Surface (Microsoft DI)

```csharp
using VapeCache.Abstractions.Connections;

builder.Services.AddOptions<RedisConnectionOptions>()
    .Bind(builder.Configuration.GetSection("RedisConnection"));

builder.Services.AddVapecacheRedisConnections();
builder.Services.AddVapecacheCaching();
```

`AddVapecacheCaching()` registers:

- `ICacheService` -> stampede-protected hybrid cache service.
- `ICacheTagService` -> same stampede-protected hybrid instance.
- `IVapeCache` -> typed client over `ICacheService`.
- `IRedisCircuitBreakerState` -> live breaker state from hybrid runtime.
- `IRedisFailoverController` -> manual failover control from hybrid runtime.
- `ICurrentCacheService` / `ICacheStats` -> runtime state and counters.

## Primary Application APIs

### `IVapeCache`

Use this for normal app code.

```csharp
public interface IVapeCache
{
    ICacheRegion Region(string name);
    ValueTask<T?> GetAsync<T>(CacheKey<T> key, CancellationToken ct = default);
    ValueTask SetAsync<T>(CacheKey<T> key, T value, CacheEntryOptions options = default, CancellationToken ct = default);
    ValueTask<T> GetOrCreateAsync<T>(CacheKey<T> key, Func<CancellationToken, ValueTask<T>> factory, CacheEntryOptions options = default, CancellationToken ct = default);
    ValueTask<bool> RemoveAsync(CacheKey key, CancellationToken ct = default);

    ValueTask<long> InvalidateTagAsync(string tag, CancellationToken ct = default);
    ValueTask<long> GetTagVersionAsync(string tag, CancellationToken ct = default);
    ValueTask<long> InvalidateZoneAsync(string zone, CancellationToken ct = default);
    ValueTask<long> GetZoneVersionAsync(string zone, CancellationToken ct = default);
}
```

Behavior:

- `Get/GetOrCreate` read through Redis when healthy.
- `Set/Remove` write to Redis when healthy and to fallback during outages.
- Tag/zone invalidation is version-based and immediate on next read.

### `ICacheService`

Low-level API for byte/delegate paths and hot-path integrations.

```csharp
public interface ICacheService
{
    string Name { get; }
    ValueTask<byte[]?> GetAsync(string key, CancellationToken ct);
    ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CacheEntryOptions options, CancellationToken ct);
    ValueTask<bool> RemoveAsync(string key, CancellationToken ct);
    ValueTask<T?> GetAsync<T>(string key, SpanDeserializer<T> deserialize, CancellationToken ct);
    ValueTask SetAsync<T>(string key, T value, Action<IBufferWriter<byte>, T> serialize, CacheEntryOptions options, CancellationToken ct);
    ValueTask<T> GetOrSetAsync<T>(string key, Func<CancellationToken, ValueTask<T>> factory, Action<IBufferWriter<byte>, T> serialize, SpanDeserializer<T> deserialize, CacheEntryOptions options, CancellationToken ct);
}
```

## Tag and Zone APIs

### `ICacheTagService`

```csharp
public interface ICacheTagService
{
    ValueTask<long> InvalidateTagAsync(string tag, CancellationToken ct = default);
    ValueTask<long> GetTagVersionAsync(string tag, CancellationToken ct = default);
    ValueTask<long> InvalidateZoneAsync(string zone, CancellationToken ct = default);
    ValueTask<long> GetZoneVersionAsync(string zone, CancellationToken ct = default);
}
```

Rules:

- Tags and zones are normalized with `Trim()`.
- Zones are encoded as reserved tags with prefix `zone:`.
- Cache entries can carry tag metadata through `CacheEntryOptions.Intent.Tags`.
- Use helpers:
  - `WithTag(...)`
  - `WithTags(...)`
  - `WithZone(...)`
  - `WithZones(...)`

## Operational APIs

### `IRedisCircuitBreakerState`

Read-only state for dashboards and health endpoints.

```csharp
public interface IRedisCircuitBreakerState
{
    bool Enabled { get; }
    bool IsOpen { get; }
    int ConsecutiveFailures { get; }
    TimeSpan? OpenRemaining { get; }
    bool HalfOpenProbeInFlight { get; }
}
```

### `IRedisFailoverController`

Manual control for drills and emergency routing.

```csharp
public interface IRedisFailoverController
{
    bool IsForcedOpen { get; }
    string? Reason { get; }
    void ForceOpen(string reason);
    void ClearForcedOpen();
    void MarkRedisSuccess();
    void MarkRedisFailure();
}
```

## Runtime Behavior Contract

`Get` operation:

- Closed: Redis first, fallback warm/read as configured.
- Open/forced-open: fallback path only.
- Half-open busy: fallback path only.
- Tagged stale entry: treated as miss and stale key is removed.

`Set` operation:

- Closed: Redis write, optional fallback mirror.
- Open/forced-open: fallback write + reconciliation tracking.
- Half-open busy: fallback write + reconciliation tracking.

`Remove` operation:

- Always removes fallback key.
- Attempts Redis remove when allowed by breaker state.
- Tracks delete for reconciliation when Redis path is unavailable.

Tag/zone invalidation:

- Increments version key (`vapecache:tag:v1:{tag}`).
- Entries written with tags carry envelope metadata of observed versions.
- Any version mismatch on read invalidates that entry immediately.

## Configuration Defaults (Hybrid)

`RedisCircuitBreakerOptions`:

- `Enabled = true`
- `ConsecutiveFailuresToOpen = 2`
- `BreakDuration = 10s`
- `HalfOpenProbeTimeout = 250ms`
- `UseExponentialBackoff = true`
- `MaxBreakDuration = 5m`
- `MaxConsecutiveRetries = 0` (infinite)
- `MaxHalfOpenProbes = 5`

`HybridFailoverOptions`:

- `MirrorWritesToFallbackWhenRedisHealthy = true`
- `WarmFallbackOnRedisReadHit = true`
- `FallbackWarmReadTtl = 2m`
- `FallbackMirrorWriteTtlWhenMissing = 5m`
- `MaxMirrorPayloadBytes = 256KB`
- `RemoveStaleFallbackOnRedisMiss = true`

`CacheStampedeOptions`:

- `Enabled = true`
- `MaxKeys = 50_000`
- `RejectSuspiciousKeys = true`
- `MaxKeyLength = 512`
- `LockWaitTimeout = 750ms`
- `EnableFailureBackoff = true`
- `FailureBackoff = 500ms`

## EF Core Second-Level Cache Pattern (Zones)

```csharp
var zone = "ef:products";
var key = CacheKey<IReadOnlyList<ProductDto>>.From("ef:q:products:list");

var list = await cache.GetOrCreateAsync(
    key,
    async ct => await db.Products.Select(p => new ProductDto(p.Id, p.Name)).ToListAsync(ct),
    new CacheEntryOptions(TimeSpan.FromMinutes(5)).WithZone(zone),
    ct);

// On SaveChanges affecting Products:
await cache.InvalidateZoneAsync(zone, ct);
```

## Production Notes

- Fallback memory is node-local; use session affinity during failover in multi-node clusters.
- Tag/zone APIs require the hybrid runtime (`AddVapecacheCaching`).
- Reconciliation durability is provided by `VapeCache.Reconciliation`; configure it for no-drop outage replay guarantees.
