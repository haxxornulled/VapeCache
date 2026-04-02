using System.Globalization;
using VapeCache.Abstractions.Modules;
using VapeCache.Features.Invalidation;
using VapeCache.Features.Search;

namespace VapeCache.Console.GroceryStore;

internal sealed class ReceiptSearchDocumentMapper : IRedisHashSearchDocumentMapper<ReceiptSearchDocument>
{
    private readonly string _documentIdPrefix;

    public ReceiptSearchDocumentMapper(
        ReceiptSearchRuntimeDescriptor? descriptor = null,
        string? documentIdPrefix = null)
    {
        var runtime = descriptor ?? ReceiptSearchRuntimeDescriptor.Default;
        _documentIdPrefix = NormalizeKeyPrefix(documentIdPrefix);
        Index = new RedisSearchIndexDefinition(
            runtime.IndexName,
            runtime.DocumentKeyPrefix,
            [
                RedisSearchFieldDefinition.Tag("orderId", sortable: true),
                RedisSearchFieldDefinition.Tag("shopperId"),
                RedisSearchFieldDefinition.Tag("sessionId"),
                RedisSearchFieldDefinition.Tag("saleId"),
                RedisSearchFieldDefinition.Tag("storeId"),
                RedisSearchFieldDefinition.Tag("receiptStatus"),
                RedisSearchFieldDefinition.Tag("fulfillmentMethod"),
                RedisSearchFieldDefinition.Tag("runScope"),
                RedisSearchFieldDefinition.Numeric("itemCount", sortable: true),
                RedisSearchFieldDefinition.Numeric("subtotal", sortable: true),
                RedisSearchFieldDefinition.Numeric("checkedOutUnixMilliseconds", sortable: true),
                RedisSearchFieldDefinition.Text("searchText", weight: 2.0)
            ]);
    }

    public RedisSearchIndexDefinition Index { get; }

    public string GetDocumentId(ReceiptSearchDocument document)
        => string.Concat(_documentIdPrefix, document.OrderId);

    public IReadOnlyList<RedisSearchHashFieldValue> MapFields(ReceiptSearchDocument document)
        =>
        [
            new("orderId", document.OrderId),
            new("shopperId", document.ShopperId),
            new("sessionId", document.SessionId),
            new("saleId", document.SaleId),
            new("storeId", document.StoreId),
            new("receiptStatus", document.ReceiptStatus),
            new("fulfillmentMethod", document.FulfillmentMethod),
            new("runScope", document.RunScope),
            new("itemCount", document.ItemCount.ToString(CultureInfo.InvariantCulture)),
            new("subtotal", document.Subtotal.ToString(CultureInfo.InvariantCulture)),
            new("checkedOutUnixMilliseconds", document.CheckedOutUnixMilliseconds.ToString(CultureInfo.InvariantCulture)),
            new("searchText", document.SearchText)
        ];

    private static string NormalizeKeyPrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return string.Empty;

        var trimmed = prefix.Trim();
        return trimmed.EndsWith(':') ? trimmed : string.Concat(trimmed, ":");
    }
}

internal sealed record ReceiptSearchRuntimeDescriptor(string IndexName, string DocumentKeyPrefix)
{
    public static ReceiptSearchRuntimeDescriptor Default { get; } = new(
        SuperCenterReceiptSearch.DefaultIndexName,
        SuperCenterReceiptSearch.DefaultDocumentKeyPrefix);

    public static ReceiptSearchRuntimeDescriptor ForComparison(string providerSegment)
        => new(
            SuperCenterReceiptSearch.ComparisonIndexName(providerSegment),
            SuperCenterReceiptSearch.ComparisonDocumentKeyPrefix(providerSegment));
}

internal sealed class ReceiptFlaggedInvalidationPolicy : ICacheInvalidationPolicy<ReceiptFlaggedForReview>
{
    private readonly ReceiptSearchRuntimeDescriptor _runtime;

    public ReceiptFlaggedInvalidationPolicy(ReceiptSearchRuntimeDescriptor runtime)
    {
        _runtime = runtime;
    }

    public ValueTask<CacheInvalidationPlan> BuildPlanAsync(
        ReceiptFlaggedForReview eventData,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        var plan = new CacheInvalidationPlanBuilder()
            .AddSearchZone(_runtime.IndexName)
            .AddSearchIndexTag(_runtime.IndexName)
            .AddSearchScopeTag(_runtime.IndexName, "shopperId", eventData.ShopperId)
            .AddSearchScopeTag(_runtime.IndexName, "storeId", eventData.StoreId)
            .AddSearchEntityTag(_runtime.IndexName, "order", eventData.OrderId)
            .AddSearchDocumentKey(eventData.SearchDocumentKey)
            .Build();

        return ValueTask.FromResult(plan);
    }
}

internal static class SuperCenterReceiptSearch
{
    public const string DefaultIndexName = "idx:grocery:receipts";
    public const string DefaultDocumentKeyPrefix = "receipt:search:doc:";
    public const string ClearedStatus = "cleared";

    public static string ComparisonIndexName(string providerSegment)
        => $"idx:cmp:{NormalizeSegment(providerSegment)}:grocery:receipts";

    public static string ComparisonDocumentKeyPrefix(string providerSegment)
        => $"cmp:{NormalizeSegment(providerSegment)}:receipt:search:doc:";

    public static ReceiptSearchDocument CreateDocument(
        GroceryCheckoutEvent checkoutEvent,
        string runScope)
    {
        ArgumentNullException.ThrowIfNull(checkoutEvent);

        return new ReceiptSearchDocument(
            checkoutEvent.OrderId,
            checkoutEvent.ShopperId,
            checkoutEvent.SessionId,
            checkoutEvent.SaleId,
            checkoutEvent.StoreId,
            checkoutEvent.ReceiptStatus,
            checkoutEvent.FulfillmentMethod,
            runScope,
            checkoutEvent.ItemCount,
            checkoutEvent.Subtotal,
            new DateTimeOffset(checkoutEvent.CheckedOutAtUtc).ToUnixTimeMilliseconds(),
            checkoutEvent.ReceiptSearchText);
    }

    public static bool IsAbsoluteSearchDocumentKey(string key)
        => !string.IsNullOrWhiteSpace(key)
           && key.Contains(":receipt:search:doc:", StringComparison.Ordinal);

    private static string NormalizeSegment(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim();
    }
}
