# Redis Search

`VapeCache.Features.Search` is the Redis search package for HASH-backed operational search workloads.

It is designed for the pattern we want in VapeCache:

- keep the authoritative runtime data in the right Redis data type
- project the search-worthy slice into small HASH documents
- index those HASH documents with RediSearch
- cache hot query-result pages in VapeCache
- invalidate those result pages instantly with `VapeCache.Features.Invalidation`

This is the right shape for the grocery-store receipt-check flow.

## Why This Package Exists

The low-level runtime already speaks `FT.CREATE` and `FT.SEARCH`, but that is not enough for a real product workflow.

Teams need:

- typed schema definitions
- projection/document mappers
- safe query construction
- result-cache key/tag conventions
- invalidation helpers that fit the existing invalidation package

That is what `VapeCache.Features.Search` adds.

## Recommended Enterprise Pattern

Use RediSearch as an operational secondary index over denormalized projections.

Recommended:

- `TAG` fields for exact-match filters and routing dimensions
- `NUMERIC` fields for time ranges, totals, counters, and sortable values
- `TEXT` fields only for the small free-text surface you actually need
- HASH projections that are intentionally smaller than the full source aggregate

Avoid:

- indexing giant JSON/cart blobs for every mutation
- treating search as the source of truth
- overusing `TEXT` for fields that should really be `TAG`
- forcing every read path through search when a direct key lookup is cheaper

## Grocery Receipt-Check Plan

For the front-door receipt checker, the optimized plan is:

1. Keep cart/order/session state in the right native Redis data types.
2. Emit a receipt-search projection per completed or active receipt/order.
3. Index that projection with a HASH-backed RediSearch index.
4. Cache hot receipt-check query results in VapeCache using search tags/zones.
5. On receipt override/flag/void/manual-clear, dispatch an invalidation policy that kills:
   - the broad search zone if needed
   - shopper/order/store scoped search tags
   - the projection HASH key when the document itself is no longer valid

That gives you fast lookup without turning the entire runtime into a search engine.

## Field Design Guidance

For grocery/retail, a good receipt-check index usually looks like this:

- `orderId`: `TAG`, sortable when exact lookup ordering matters
- `shopperId`: `TAG`
- `storeId`: `TAG`
- `laneId` or `terminalId`: `TAG`
- `receiptStatus`: `TAG`
- `checkedOutTicks`: `NUMERIC`, sortable
- `subtotal`: `NUMERIC`, sortable
- `itemCount`: `NUMERIC`
- `searchText`: `TEXT`

Use `TAG` for values like status, shopper id, store id, fulfillment method, or order id.
Use `NUMERIC` for ticks, totals, and counts.
Reserve `TEXT` for the small human-searchable field set.

## Package Surface

Main types:

- `VapeCacheSearchOptions`
- `RedisSearchIndexDefinition`
- `RedisSearchQuery`
- `RedisSearchQueryBuilder`
- `RedisSearchConventions`
- `IRedisHashSearchDocumentMapper<TDocument>`
- `IRedisHashSearchDocumentStore<TDocument>`
- `RedisHashSearchDocumentStore<TDocument>`

DI:

```csharp
builder.Services.AddVapeCacheSearch(builder.Configuration);
```

Config section:

```json
{
  "VapeCache": {
    "Search": {
      "Enabled": true,
      "RequireModuleAvailability": false,
      "DefaultResultCount": 20
    }
  }
}
```

## Invalidation Pattern

The search package intentionally does not invent a parallel invalidation engine.
It plugs into `VapeCache.Features.Invalidation`.

Typical policy shape:

```csharp
public sealed class ReceiptFlaggedInvalidationPolicy : ICacheInvalidationPolicy<ReceiptFlagged>
{
    public ValueTask<CacheInvalidationPlan> BuildPlanAsync(ReceiptFlagged eventData, CancellationToken ct = default)
        => ValueTask.FromResult(new CacheInvalidationPlanBuilder()
            .AddSearchZone("idx:grocery:receipts")
            .AddSearchIndexTag("idx:grocery:receipts")
            .AddSearchScopeTag("idx:grocery:receipts", "shopperId", eventData.ShopperId)
            .AddSearchEntityTag("idx:grocery:receipts", "order", eventData.OrderId)
            .AddSearchDocumentKey(RedisSearchConventions.DocumentKey("receipt:search:doc:", eventData.OrderId))
            .Build());
}
```

This is the important split:

- invalidate cached result pages with tags/zones
- delete or rewrite the underlying projection document only when the actual searchable state changed

## Compatibility Contract

What this package guarantees:

- typed HASH-backed RediSearch index definitions
- query-builder helpers for common exact-match/text/range filters
- projection-store helpers that keep search documents in Redis hashes
- invalidation conventions that work with `VapeCache.Features.Invalidation`

What it does not try to be:

- a general-purpose search abstraction over unrelated backends
- a promise of provider parity with non-Redis search systems
- a replacement for direct key lookups when the answer is already addressable by key

## Related Docs

- [CACHE_INVALIDATION.md](CACHE_INVALIDATION.md)
- [NUGET_PACKAGES.md](NUGET_PACKAGES.md)
- [API_REFERENCE.md](API_REFERENCE.md)
