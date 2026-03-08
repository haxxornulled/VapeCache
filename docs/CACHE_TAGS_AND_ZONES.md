# Cache Tags and Zones

VapeCache supports versioned tag invalidation in the core cache runtime.

- `Tags`: arbitrary grouping labels you attach to cache entries.
- `Zones`: a first-class convention for broad cache groups (implemented as reserved tags using `zone:` prefix).

When a tag/zone is invalidated, matching entries become stale immediately without full-key scans.

## API Surface

```csharp
using VapeCache.Abstractions.Caching;
```

- `CacheEntryOptions.WithTag(...)`
- `CacheEntryOptions.WithTags(...)`
- `CacheEntryOptions.WithZone(...)`
- `CacheEntryOptions.WithZones(...)`
- `IVapeCache.InvalidateTagAsync(...)`
- `IVapeCache.GetTagVersionAsync(...)`
- `IVapeCache.InvalidateZoneAsync(...)`
- `IVapeCache.GetZoneVersionAsync(...)`

## Example: Tag Invalidation

```csharp
var options = new CacheEntryOptions(TimeSpan.FromMinutes(10))
    .WithTags("catalog", "product:42");

await cache.SetAsync(CacheKey<ProductDto>.From("product:42"), dto, options, ct);

await cache.InvalidateTagAsync("catalog", ct);
```

## Example: EF Core Second-Level Cache Zones

Use zones to group query caches by table/aggregate.

```csharp
var productsZone = "ef:products";
var productListKey = CacheKey<IReadOnlyList<ProductDto>>.From("ef:q:products:list");

var list = await cache.GetOrCreateAsync(
    productListKey,
    async ct => await db.Products.Select(p => new ProductDto(p.Id, p.Name)).ToListAsync(ct),
    new CacheEntryOptions(TimeSpan.FromMinutes(5)).WithZone(productsZone),
    ct);

// On write (SaveChanges), invalidate all product-derived queries:
await cache.InvalidateZoneAsync(productsZone, ct);
```

## Notes

- Zone names are normalized (`Trim`) and stored as `zone:{name}` tags.
- Existing tags are preserved when `WithTags/WithZones` are chained.
- Tag/zone operations require `AddVapecacheCaching()` (Hybrid cache runtime).
