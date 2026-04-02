using VapeCache.Abstractions.Caching;

namespace VapeCache.Console.GroceryStore;

public interface IGroceryStoreRecentlyViewedOperations
{
    ValueTask TrackRecentlyViewedAsync(
        string shopperId,
        IReadOnlyList<string> productIds,
        DateTime viewedAtUtc,
        CancellationToken ct = default);

    ValueTask<string[]> GetRecentlyViewedProductIdsAsync(
        string shopperId,
        int take,
        CancellationToken ct = default);
}

public interface IGroceryStoreCheckoutEventOperations
{
    ValueTask RecordCheckoutAsync(GroceryCheckoutEvent checkoutEvent, CancellationToken ct = default);
}

public interface IGroceryStoreReceiptCheckOperations
{
    ValueTask<ReceiptExitCheckResult> CheckReceiptAtExitAsync(
        ReceiptExitCheckRequest request,
        CancellationToken ct = default);
}

internal sealed record ShopperScopeInvalidationRequested(string ShopperId);
internal sealed record ReceiptFlaggedForReview(string OrderId, string ShopperId, string StoreId, string SearchDocumentKey);

internal static class SuperCenterKeySpace
{
    public static string Product(string productId)
        => $"product:{productId}";

    public static string Cart(string shopperId)
        => $"cart:{shopperId}";

    public static string Session(string sessionId)
        => $"session:{sessionId}";

    public static string FlashSaleParticipants(string saleId)
        => $"sale:{saleId}:participants";

    public static string RecentlyViewed(string shopperId)
        => $"shopper:{shopperId}:recent";

    public static string CheckoutStream(string shopperId)
        => $"checkout:{shopperId}:events";

    public static string ShopperTag(string shopperId)
        => $"shopper:{shopperId}";

    public static string ShopperTag(string shopperId, string scope)
        => $"shopper:{shopperId}:{scope}";
}

internal sealed class SuperCenterInvalidationVapeCacheBridge : IVapeCache
{
    private readonly ISuperCenterStoreProvider _provider;

    public SuperCenterInvalidationVapeCacheBridge(ISuperCenterStoreProvider provider)
    {
        _provider = provider;
    }

    public ICacheRegion Region(string name)
        => throw new NotSupportedException(
            "The SuperCenter invalidation bridge only supports tag, zone, and key invalidation operations.");

    public ValueTask<T?> GetAsync<T>(CacheKey<T> key, CancellationToken ct = default)
        => throw new NotSupportedException(
            "The SuperCenter invalidation bridge does not support cache reads.");

    public ValueTask SetAsync<T>(CacheKey<T> key, T value, CacheEntryOptions options = default, CancellationToken ct = default)
        => throw new NotSupportedException(
            "The SuperCenter invalidation bridge does not support cache writes.");

    public ValueTask<T> GetOrCreateAsync<T>(
        CacheKey<T> key,
        Func<CancellationToken, ValueTask<T>> factory,
        CacheEntryOptions options = default,
        CancellationToken ct = default)
        => throw new NotSupportedException(
            "The SuperCenter invalidation bridge does not support cache factories.");

    public ValueTask<bool> RemoveAsync(CacheKey key, CancellationToken ct = default)
        => _provider.RemoveKeyAsync(key.Value, ct);

    public ValueTask<long> InvalidateTagAsync(string tag, CancellationToken ct = default)
        => _provider.InvalidateTagAsync(tag, ct);

    public ValueTask<long> GetTagVersionAsync(string tag, CancellationToken ct = default)
        => ValueTask.FromResult(0L);

    public ValueTask<long> InvalidateZoneAsync(string zone, CancellationToken ct = default)
        => _provider.InvalidateTagAsync(CacheTagConventions.ToZoneTag(zone), ct);

    public ValueTask<long> GetZoneVersionAsync(string zone, CancellationToken ct = default)
        => ValueTask.FromResult(0L);
}
