using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using VapeCache.Features.Invalidation;

namespace VapeCache.Console.GroceryStore;

public interface IGroceryStoreTagOperations
{
    ValueTask<long> InvalidateShopperScopeAsync(string shopperId, CancellationToken ct = default);
}

/// <summary>
/// Shared apples-to-apples grocery store workload.
/// Both VapeCache and StackExchange.Redis run through the same service logic,
/// with only the storage provider swapped underneath.
/// </summary>
internal sealed class SuperCenterGroceryStoreService : IGroceryStoreService, ICartBatchWriter,
    IGroceryStoreComparisonTelemetrySource, IGroceryStoreTagOperations, IGroceryStoreCommandCoverageRunner,
    IGroceryStoreRecentlyViewedOperations, IGroceryStoreCheckoutEventOperations, IGroceryStoreReceiptCheckOperations
{
    private readonly ISuperCenterStoreProvider _provider;
    private readonly ICacheInvalidationDispatcher _invalidationDispatcher;
    private readonly ILogger<SuperCenterGroceryStoreService> _logger;
    private readonly ConcurrentDictionary<string, CartItem[]> _cartShadow = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, UserSession> _sessionShadow = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string[]> _recentlyViewedShadow = new(StringComparer.Ordinal);
    private static readonly bool AssumeEmptyCartOnFirstWrite = ResolveAssumeEmptyCartOnFirstWrite();
    private static readonly Product[] Products = GroceryStoreService.GetAllProducts();
    private static readonly IReadOnlyDictionary<string, Product> ProductsById = BuildProductMap(Products);
    private long _productReadOps;
    private long _productWriteOps;
    private long _cartReadOps;
    private long _cartCountReadOps;
    private long _cartItemWriteOps;
    private long _cartClearWriteOps;
    private long _flashSaleJoinWriteOps;
    private long _flashSaleMembershipReadOps;
    private long _flashSaleParticipantCountReadOps;
    private long _sessionReadOps;
    private long _sessionWriteOps;
    private long _recentlyViewedReadOps;
    private long _recentlyViewedWriteOps;
    private long _checkoutEventWriteOps;
    private long _receiptSearchReadOps;
    private long _receiptReviewInvalidationWriteOps;
    private long _tagInvalidationWriteOps;
    private long _commandCoverageReadOps;
    private long _commandCoverageWriteOps;
    private long _commandCoverageAdminOps;
    private long _commandCoverageOptionalSkips;

    public SuperCenterGroceryStoreService(
        ISuperCenterStoreProvider provider,
        ICacheInvalidationDispatcher invalidationDispatcher,
        ILogger<SuperCenterGroceryStoreService> logger)
    {
        _provider = provider;
        _invalidationDispatcher = invalidationDispatcher;
        _logger = logger;
    }

    public async ValueTask<Product?> GetProductAsync(string productId)
    {
        Interlocked.Increment(ref _productReadOps);
        var cached = await _provider.GetProductAsync(productId, CancellationToken.None).ConfigureAwait(false);
        if (cached is not null)
            return cached;

        if (!ProductsById.TryGetValue(productId, out var product))
            return null;

        await _provider.CacheProductAsync(product, TimeSpan.FromMinutes(10), CancellationToken.None).ConfigureAwait(false);
        return product;
    }

    public async ValueTask CacheProductAsync(Product product, TimeSpan ttl)
    {
        Interlocked.Increment(ref _productWriteOps);
        await _provider.CacheProductAsync(product, ttl, CancellationToken.None).ConfigureAwait(false);
    }

    public async ValueTask AddToCartAsync(string userId, CartItem item)
        => await AddToCartBatchAsync(userId, [item]).ConfigureAwait(false);

    public async ValueTask AddToCartBatchAsync(string userId, IReadOnlyList<CartItem> items)
    {
        if (items.Count == 0)
            return;

        Interlocked.Add(ref _cartItemWriteOps, items.Count);

        if (!_cartShadow.TryGetValue(userId, out var existing))
        {
            if (AssumeEmptyCartOnFirstWrite)
                existing = Array.Empty<CartItem>();
            else
                existing = await _provider.GetCartAsync(userId, CancellationToken.None).ConfigureAwait(false);
        }

        CartItem[] merged;
        if (existing.Length == 0 && items is CartItem[] directItems)
        {
            merged = directItems;
        }
        else
        {
            merged = new CartItem[existing.Length + items.Count];
            if (existing.Length > 0)
                Array.Copy(existing, merged, existing.Length);
            for (var i = 0; i < items.Count; i++)
                merged[existing.Length + i] = items[i];
        }

        await _provider.SetCartAsync(userId, merged, TimeSpan.FromMinutes(30), CancellationToken.None).ConfigureAwait(false);
        _cartShadow[userId] = merged;
    }

    public async ValueTask<CartItem[]> GetCartAsync(string userId)
    {
        Interlocked.Increment(ref _cartReadOps);
        if (_cartShadow.TryGetValue(userId, out var shadow))
            return shadow;

        return await _provider.GetCartAsync(userId, CancellationToken.None).ConfigureAwait(false);
    }

    public async ValueTask<long> GetCartCountAsync(string userId)
    {
        Interlocked.Increment(ref _cartCountReadOps);
        if (_cartShadow.TryGetValue(userId, out var shadow))
            return shadow.Length;

        return await _provider.GetCartCountAsync(userId, CancellationToken.None).ConfigureAwait(false);
    }

    public async ValueTask ClearCartAsync(string userId)
    {
        Interlocked.Increment(ref _cartClearWriteOps);
        _cartShadow.TryRemove(userId, out _);
        await _provider.RemoveCartAsync(userId, CancellationToken.None).ConfigureAwait(false);
    }

    public async ValueTask JoinFlashSaleAsync(string saleId, string userId)
    {
        Interlocked.Increment(ref _flashSaleJoinWriteOps);
        await _provider.JoinFlashSaleAsync(saleId, userId, CancellationToken.None).ConfigureAwait(false);
    }

    public async ValueTask<bool> IsInFlashSaleAsync(string saleId, string userId)
    {
        Interlocked.Increment(ref _flashSaleMembershipReadOps);
        return await _provider.IsInFlashSaleAsync(saleId, userId, CancellationToken.None).ConfigureAwait(false);
    }

    public async ValueTask<long> GetFlashSaleParticipantCountAsync(string saleId)
    {
        Interlocked.Increment(ref _flashSaleParticipantCountReadOps);
        return await _provider.GetFlashSaleParticipantCountAsync(saleId, CancellationToken.None).ConfigureAwait(false);
    }

    public async ValueTask SaveSessionAsync(string sessionId, UserSession session)
    {
        Interlocked.Increment(ref _sessionWriteOps);
        await _provider.SaveSessionAsync(sessionId, session, TimeSpan.FromHours(1), CancellationToken.None).ConfigureAwait(false);
        _sessionShadow[sessionId] = session;
    }

    public async ValueTask<UserSession?> GetSessionAsync(string sessionId)
    {
        Interlocked.Increment(ref _sessionReadOps);
        if (_sessionShadow.TryGetValue(sessionId, out var shadow))
            return shadow;

        return await _provider.GetSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
    }

    public async ValueTask TrackRecentlyViewedAsync(
        string shopperId,
        IReadOnlyList<string> productIds,
        DateTime viewedAtUtc,
        CancellationToken ct = default)
    {
        if (productIds.Count == 0)
            return;

        Interlocked.Increment(ref _recentlyViewedWriteOps);
        await _provider.TrackRecentlyViewedAsync(
            shopperId,
            productIds,
            viewedAtUtc,
            TimeSpan.FromMinutes(30),
            ct).ConfigureAwait(false);

        var snapshot = new string[productIds.Count];
        for (var i = 0; i < productIds.Count; i++)
            snapshot[i] = productIds[i];

        _recentlyViewedShadow[shopperId] = snapshot;
    }

    public async ValueTask<string[]> GetRecentlyViewedProductIdsAsync(
        string shopperId,
        int take,
        CancellationToken ct = default)
    {
        Interlocked.Increment(ref _recentlyViewedReadOps);
        if (_recentlyViewedShadow.TryGetValue(shopperId, out var shadow))
            return shadow.Take(Math.Max(0, take)).ToArray();

        return await _provider.GetRecentlyViewedAsync(shopperId, take, ct).ConfigureAwait(false);
    }

    public async ValueTask RecordCheckoutAsync(GroceryCheckoutEvent checkoutEvent, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _checkoutEventWriteOps);
        await _provider.RecordCheckoutEventAsync(checkoutEvent, TimeSpan.FromHours(1), ct).ConfigureAwait(false);
    }

    public async ValueTask<ReceiptExitCheckResult> CheckReceiptAtExitAsync(
        ReceiptExitCheckRequest request,
        CancellationToken ct = default)
    {
        Interlocked.Increment(ref _receiptSearchReadOps);
        var lookup = await _provider.SearchReceiptAsync(request, ct).ConfigureAwait(false);
        long invalidatedTargets = 0;
        if (request.FlagForManualReview && lookup.Matched)
        {
            Interlocked.Increment(ref _receiptReviewInvalidationWriteOps);
            var result = await _invalidationDispatcher
                .DispatchAsync(
                    new ReceiptFlaggedForReview(
                        request.OrderId,
                        request.ShopperId,
                        request.StoreId,
                        lookup.SearchDocumentKey),
                    ct)
                .ConfigureAwait(false);
            invalidatedTargets = result.InvalidatedTargets;
        }

        return new ReceiptExitCheckResult(
            lookup.Matched,
            lookup.HitCount,
            invalidatedTargets,
            request.FlagForManualReview);
    }

    public async ValueTask<long> InvalidateShopperScopeAsync(string shopperId, CancellationToken ct = default)
    {
        _cartShadow.TryRemove(shopperId, out _);
        _sessionShadow.TryRemove(shopperId, out _);
        _recentlyViewedShadow.TryRemove(shopperId, out _);
        Interlocked.Increment(ref _tagInvalidationWriteOps);
        var result = await _invalidationDispatcher
            .DispatchAsync(new ShopperScopeInvalidationRequested(shopperId), ct)
            .ConfigureAwait(false);
        return result.InvalidatedTargets;
    }

    public async ValueTask ExecuteShopperCommandCoverageAsync(
        string shopperId,
        string saleId,
        string sessionId,
        DateTime timestampUtc,
        CartItem[] items,
        CancellationToken ct = default)
    {
        var snapshot = await _provider.ExecuteCommandCoverageAsync(
            new SuperCenterCommandCoverageContext(
                shopperId,
                saleId,
                sessionId,
                timestampUtc,
                items),
            ct).ConfigureAwait(false);

        if (snapshot.ReadOps != 0)
            Interlocked.Add(ref _commandCoverageReadOps, snapshot.ReadOps);
        if (snapshot.WriteOps != 0)
            Interlocked.Add(ref _commandCoverageWriteOps, snapshot.WriteOps);
        if (snapshot.AdminOps != 0)
            Interlocked.Add(ref _commandCoverageAdminOps, snapshot.AdminOps);
        if (snapshot.OptionalSkips != 0)
            Interlocked.Add(ref _commandCoverageOptionalSkips, snapshot.OptionalSkips);
    }

    public GroceryStoreComparisonTelemetrySnapshot GetTelemetrySnapshot()
    {
        return new GroceryStoreComparisonTelemetrySnapshot(
            ProductReadOps: Volatile.Read(ref _productReadOps),
            ProductWriteOps: Volatile.Read(ref _productWriteOps),
            CartReadOps: Volatile.Read(ref _cartReadOps),
            CartCountReadOps: Volatile.Read(ref _cartCountReadOps),
            CartItemWriteOps: Volatile.Read(ref _cartItemWriteOps),
            CartClearWriteOps: Volatile.Read(ref _cartClearWriteOps),
            FlashSaleJoinWriteOps: Volatile.Read(ref _flashSaleJoinWriteOps),
            FlashSaleMembershipReadOps: Volatile.Read(ref _flashSaleMembershipReadOps),
            FlashSaleParticipantCountReadOps: Volatile.Read(ref _flashSaleParticipantCountReadOps),
            SessionReadOps: Volatile.Read(ref _sessionReadOps),
            SessionWriteOps: Volatile.Read(ref _sessionWriteOps),
            RecentlyViewedReadOps: Volatile.Read(ref _recentlyViewedReadOps),
            RecentlyViewedWriteOps: Volatile.Read(ref _recentlyViewedWriteOps),
            CheckoutEventWriteOps: Volatile.Read(ref _checkoutEventWriteOps),
            ReceiptSearchReadOps: Volatile.Read(ref _receiptSearchReadOps),
            ReceiptReviewInvalidationWriteOps: Volatile.Read(ref _receiptReviewInvalidationWriteOps),
            TagInvalidationWriteOps: Volatile.Read(ref _tagInvalidationWriteOps),
            CommandCoverageReadOps: Volatile.Read(ref _commandCoverageReadOps),
            CommandCoverageWriteOps: Volatile.Read(ref _commandCoverageWriteOps),
            CommandCoverageAdminOps: Volatile.Read(ref _commandCoverageAdminOps),
            CommandCoverageOptionalSkips: Volatile.Read(ref _commandCoverageOptionalSkips));
    }

    private static IReadOnlyDictionary<string, Product> BuildProductMap(Product[] products)
    {
        var map = new Dictionary<string, Product>(products.Length, StringComparer.Ordinal);
        foreach (var product in products)
            map[product.Id] = product;

        return map;
    }

    private static bool ResolveAssumeEmptyCartOnFirstWrite()
    {
        var env = Environment.GetEnvironmentVariable("VAPECACHE_BENCH_ASSUME_EMPTY_CART_ON_FIRST_WRITE");
        if (bool.TryParse(env, out var parsed))
            return parsed;
        return true;
    }
}
