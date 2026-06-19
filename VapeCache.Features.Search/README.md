# VapeCache.Features.Search

Typed RediSearch helpers for HASH-backed VapeCache search projections.

## Install

```bash
dotnet add package VapeCache.Features.Search
```

## Use This Package When

- denormalized HASH documents that RediSearch can index efficiently
- typed `TEXT`, `TAG`, and `NUMERIC` schemas
- query-building helpers for exact-match and range-heavy workloads
- search-result cache key/tag conventions that work with `VapeCache.Features.Invalidation`

## Recommended Enterprise Pattern

Use this package for projection search, not as a generic document database:

1. Write the authoritative order/cart/session state with the right Redis data type.
2. Maintain a small HASH projection for the searchable slice.
3. Index that HASH prefix with RediSearch.
4. Cache hot query-result pages in VapeCache using search tags/zones.
5. Invalidate those result pages instantly with policy-driven invalidation when the projection changes.

For the grocery receipt-check use case, that means indexing receipt/order summary projections, not every cart mutation as a giant blob.

## Example

```csharp
builder.Services.AddVapeCache(builder.Configuration);
builder.Services.AddVapeCacheInvalidation();
builder.Services.AddVapeCacheSearch();

builder.Services.AddSingleton<IRedisHashSearchDocumentMapper<ReceiptSearchDocument>, ReceiptSearchDocumentMapper>();
```

```csharp
public sealed record ReceiptSearchDocument(
    string OrderId,
    string ShopperId,
    string StoreId,
    string ReceiptStatus,
    decimal Subtotal,
    long CheckedOutTicks,
    string SearchText);

public sealed class ReceiptSearchDocumentMapper : IRedisHashSearchDocumentMapper<ReceiptSearchDocument>
{
    public RedisSearchIndexDefinition Index { get; } = new(
        indexName: "idx:grocery:receipts",
        documentKeyPrefix: "receipt:search:doc:",
        fields:
        [
            RedisSearchFieldDefinition.Tag("orderId", sortable: true),
            RedisSearchFieldDefinition.Tag("shopperId"),
            RedisSearchFieldDefinition.Tag("storeId"),
            RedisSearchFieldDefinition.Tag("receiptStatus"),
            RedisSearchFieldDefinition.Numeric("subtotal", sortable: true),
            RedisSearchFieldDefinition.Numeric("checkedOutTicks", sortable: true),
            RedisSearchFieldDefinition.Text("searchText", weight: 2.0)
        ]);

    public string GetDocumentId(ReceiptSearchDocument document) => document.OrderId;

    public IReadOnlyList<RedisSearchHashFieldValue> MapFields(ReceiptSearchDocument document) =>
    [
        new("orderId", document.OrderId),
        new("shopperId", document.ShopperId),
        new("storeId", document.StoreId),
        new("receiptStatus", document.ReceiptStatus),
        new("subtotal", document.Subtotal.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new("checkedOutTicks", document.CheckedOutTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new("searchText", document.SearchText)
    ];
}
```

```csharp
var store = app.Services.GetRequiredService<IRedisHashSearchDocumentStore<ReceiptSearchDocument>>();
await store.EnsureIndexAsync(ct);

var query = new RedisSearchQueryBuilder()
    .Tag("shopperId", shopperId)
    .Tag("receiptStatus", "cleared")
    .NumericRange("checkedOutTicks", min: DateTime.UtcNow.AddHours(-2).Ticks)
    .Build(offset: 0, count: 20);

var ids = await store.SearchIdsAsync(query, ct);
```

## Compatibility Contract

- This package is optimized for Redis HASH projections plus RediSearch indexes.
- It is meant for operational search and lookup workloads, not full OLAP or arbitrary document querying.
- Result-page caching and invalidation are first-class; backend storage-format parity with unrelated search systems is not a goal.

## Invalidation Integration

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

## Docs

- Redis search guide: https://github.com/haxxornulled/VapeCache/blob/main/docs/REDIS_SEARCH.md
- Cache invalidation guide: https://github.com/haxxornulled/VapeCache/blob/main/docs/CACHE_INVALIDATION.md
- Package matrix: https://github.com/haxxornulled/VapeCache/blob/main/docs/NUGET_PACKAGES.md
