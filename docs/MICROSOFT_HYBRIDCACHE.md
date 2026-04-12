# Microsoft HybridCache Support

VapeCache can now back `Microsoft.Extensions.Caching.Hybrid.HybridCache`.

This is implemented as a native VapeCache-backed adapter, not by swapping in a separate Redis client.

## What This Gives You

- `HybridCache` registration over the native VapeCache runtime
- `GetOrCreateAsync`
- `SetAsync`
- `RemoveAsync`
- `RemoveByTagAsync`
- logical clear-all semantics via `RemoveByTagAsync("*")`

The adapter uses:

- VapeCache typed cache APIs
- VapeCache tag invalidation
- the native Redis-first hybrid runtime underneath

## Registration

```csharp
using Microsoft.Extensions.Caching.Hybrid;
using VapeCache.Extensions.DependencyInjection;

builder.Services.AddVapeCache(builder.Configuration)
    .AddMicrosoftHybridCache(options =>
    {
        options.KeyPrefix = "ms-hc:";
    });
```

In-memory-only hosts work too:

```csharp
builder.Services.AddVapeCacheInMemory()
    .AddMicrosoftHybridCache();
```

Then inject `HybridCache` as usual:

```csharp
app.MapGet("/products/{id:int}", async (int id, HybridCache cache, CancellationToken ct) =>
{
    var product = await cache.GetOrCreateAsync(
        $"products:{id}",
        async token => await LoadProductAsync(id, token),
        cancellationToken: ct);

    return Results.Ok(product);
});
```

## Tagging And Clear-All

Tags are mapped to VapeCache tag invalidation.

```csharp
await cache.SetAsync("catalog:42", product, tags: ["catalog"], cancellationToken: ct);
await cache.RemoveByTagAsync("catalog", ct);
```

For clear-all semantics on HybridCache-managed entries:

```csharp
await cache.RemoveByTagAsync("*", ct);
```

Implementation note:

- HybridCache entries registered through this adapter automatically carry an internal global tag
- `RemoveByTagAsync("*")` invalidates that internal tag
- this logically clears entries managed by the HybridCache adapter without scanning all backend keys

## Option Mapping

`HybridCacheEntryOptions` currently maps as follows:

- `Expiration` -> VapeCache TTL
- `LocalCacheExpiration` -> used as TTL when `Expiration` is not supplied
- `tags` -> VapeCache tags
- `Flags.DisableUnderlyingData` -> respected on cache misses

Current limitations:

- local/distributed split flags are not modeled independently because VapeCache uses its own native hybrid runtime, not the Microsoft reference implementation shape
- compression-related flags are not separately modeled here

That means this is best described as strong `HybridCache` API compatibility over the native VapeCache runtime, not byte-for-byte behavior parity with every possible `HybridCache` implementation detail.

## Relationship To `IDistributedCache`

VapeCache already ships an `IDistributedCache` / `IBufferDistributedCache` adapter.

There are now two valid integration stories:

- use `IDistributedCache` when an existing framework or app abstraction already depends on it
- use `HybridCache` when you want the Microsoft higher-level cache abstraction directly

Both routes stay on top of the native VapeCache runtime.
