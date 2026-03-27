using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

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
    IGroceryStoreComparisonTelemetrySource, IGroceryStoreTagOperations, IGroceryStoreCommandCoverageRunner
{
    private readonly ISuperCenterStoreProvider _provider;
    private readonly ILogger<SuperCenterGroceryStoreService> _logger;
    private readonly ConcurrentDictionary<string, CartItem[]> _cartShadow = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, UserSession> _sessionShadow = new(StringComparer.Ordinal);
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
    private long _commandCoverageReadOps;
    private long _commandCoverageWriteOps;
    private long _commandCoverageAdminOps;
    private long _commandCoverageOptionalSkips;

    public SuperCenterGroceryStoreService(
        ISuperCenterStoreProvider provider,
        ILogger<SuperCenterGroceryStoreService> logger)
    {
        _provider = provider;
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

    public async ValueTask<long> InvalidateShopperScopeAsync(string shopperId, CancellationToken ct = default)
    {
        _cartShadow.TryRemove(shopperId, out _);
        _sessionShadow.TryRemove(shopperId, out _);
        return await _provider.InvalidateTagAsync($"shopper:{shopperId}", ct).ConfigureAwait(false);
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
