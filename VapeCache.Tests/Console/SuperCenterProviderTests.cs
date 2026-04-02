using Microsoft.Extensions.DependencyInjection;
using VapeCache.Abstractions.Caching;
using VapeCache.Console.GroceryStore;
using VapeCache.Features.Invalidation;
using VapeCache.Features.Search;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Tests.Console;

public sealed class SuperCenterProviderTests
{
    [Fact]
    public async Task VapeCacheProvider_StoresCartAsList_AndRecentlyViewedAsSortedSet()
    {
        await using var executor = new InMemoryCommandExecutor();
        var provider = new VapeCacheSuperCenterProvider(executor);
        var now = new DateTime(2026, 3, 31, 12, 0, 0, DateTimeKind.Utc);

        await provider.SetCartAsync(
            "shopper-1",
            [
                new CartItem("prod-001", "Organic Bananas", 2.99m, 2, now),
                new CartItem("prod-006", "Organic Whole Milk", 4.29m, 1, now)
            ],
            TimeSpan.FromMinutes(30),
            CancellationToken.None);
        await provider.TrackRecentlyViewedAsync(
            "shopper-1",
            ["prod-001", "prod-006", "prod-010"],
            now,
            TimeSpan.FromMinutes(30),
            CancellationToken.None);

        var cartCount = await provider.GetCartCountAsync("shopper-1", CancellationToken.None);
        var recent = await provider.GetRecentlyViewedAsync("shopper-1", 3, CancellationToken.None);
        var rawListLength = await executor.LLenAsync(SuperCenterKeySpace.Cart("shopper-1"), CancellationToken.None);
        var rawRecentCount = await executor.ZCardAsync(SuperCenterKeySpace.RecentlyViewed("shopper-1"), CancellationToken.None);

        Assert.Equal(2, cartCount);
        Assert.Equal(2, rawListLength);
        Assert.Equal(3, rawRecentCount);
        Assert.Equal(["prod-010", "prod-006", "prod-001"], recent);
    }

    [Fact]
    public async Task SuperCenterService_InvalidatesShopperScope_ThroughInvalidationPackage()
    {
        await using var executor = new InMemoryCommandExecutor();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISuperCenterStoreProvider>(_ => new VapeCacheSuperCenterProvider(executor));
        services.AddSingleton<IVapeCache, SuperCenterInvalidationVapeCacheBridge>();
        services.AddVapeCacheInvalidation(configure: options =>
        {
            options.Enabled = true;
            options.EnableTagInvalidation = true;
            options.EnableZoneInvalidation = false;
            options.EnableKeyInvalidation = false;
            options.Profile = CacheInvalidationProfile.HighTrafficSite;
        });
        services.AddTagInvalidationPolicy<ShopperScopeInvalidationRequested>(
            static request => [SuperCenterKeySpace.ShopperTag(request.ShopperId)]);
        services.AddSingleton<SuperCenterGroceryStoreService>();

        await using var root = services.BuildServiceProvider();
        var service = root.GetRequiredService<SuperCenterGroceryStoreService>();
        var provider = root.GetRequiredService<ISuperCenterStoreProvider>();
        var now = new DateTime(2026, 3, 31, 12, 30, 0, DateTimeKind.Utc);

        await service.AddToCartBatchAsync(
            "shopper-2",
            [
                new CartItem("prod-003", "Romaine Lettuce", 3.49m, 1, now),
                new CartItem("prod-011", "Sourdough Bread", 4.99m, 1, now)
            ]);
        await service.SaveSessionAsync(
            "shopper-2",
            new UserSession("shopper-2", "session-2", now, now, ["prod-003"], SuperCenterKeySpace.Cart("shopper-2"))
            {
                StoreId = "store-007",
                LoyaltyTier = "Gold",
                FulfillmentMethod = "Pickup"
            });
        await service.TrackRecentlyViewedAsync("shopper-2", ["prod-003", "prod-011"], now, CancellationToken.None);

        _ = await service.InvalidateShopperScopeAsync("shopper-2", CancellationToken.None);

        var session = await provider.GetSessionAsync("shopper-2", CancellationToken.None);
        var cartCount = await provider.GetCartCountAsync("shopper-2", CancellationToken.None);
        var recent = await provider.GetRecentlyViewedAsync("shopper-2", 5, CancellationToken.None);

        Assert.Null(session);
        Assert.Equal(0, cartCount);
        Assert.Empty(recent);
    }

    [Fact]
    public async Task VapeCacheProvider_RecordCheckout_WritesReceiptProjectionThroughSearchStore()
    {
        await using var executor = new InMemoryCommandExecutor();
        var store = new RecordingReceiptSearchStore("cmp:test:");
        var provider = new VapeCacheSuperCenterProvider(
            executor,
            receiptSearchDocuments: store,
            receiptSearchDocumentIdPrefix: "cmp:test:");
        var checkedOutAt = new DateTime(2026, 3, 31, 14, 0, 0, DateTimeKind.Utc);

        await provider.RecordCheckoutEventAsync(
            new GroceryCheckoutEvent(
                "order-100",
                "shopper-100",
                "session-100",
                "sale-4",
                18,
                129.42m,
                checkedOutAt)
            {
                StoreId = "store-004",
                FulfillmentMethod = "Pickup",
                ReceiptStatus = SuperCenterReceiptSearch.ClearedStatus,
                ReceiptSearchText = "order-100 shopper-100 store-004 pickup apples milk bread"
            },
            TimeSpan.FromHours(1),
            CancellationToken.None);

        Assert.True(store.EnsureIndexCalled);
        Assert.Equal("cmp:test:order-100", store.LastDocumentId);
        Assert.NotNull(store.LastDocument);
        Assert.Equal("shopper-100", store.LastDocument!.ShopperId);
        Assert.Equal("store-004", store.LastDocument.StoreId);
        Assert.Equal("Pickup", store.LastDocument.FulfillmentMethod);
        Assert.Equal(SuperCenterReceiptSearch.ClearedStatus, store.LastDocument.ReceiptStatus);
        Assert.Equal(TimeSpan.FromHours(1), store.LastTtl);
        Assert.Equal(
            new DateTimeOffset(checkedOutAt).ToUnixTimeMilliseconds(),
            store.LastDocument.CheckedOutUnixMilliseconds);
    }

    [Fact]
    public async Task SuperCenterService_CheckReceiptAtExit_InvalidatesReceiptProjectionThroughPolicy()
    {
        var provider = new ReceiptInvalidationTestProvider("cmp:vape:receipt:search:doc:cmp:test:order-200");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISuperCenterStoreProvider>(provider);
        services.AddSingleton<IVapeCache, SuperCenterInvalidationVapeCacheBridge>();
        services.AddVapeCacheInvalidation(configure: options =>
        {
            options.Enabled = true;
            options.EnableTagInvalidation = true;
            options.EnableZoneInvalidation = false;
            options.EnableKeyInvalidation = true;
            options.Profile = CacheInvalidationProfile.HighTrafficSite;
        });
        services.AddCacheInvalidationPolicy<ReceiptFlaggedForReview>(
            _ => new ReceiptFlaggedInvalidationPolicy(ReceiptSearchRuntimeDescriptor.ForComparison("vape")));
        services.AddSingleton<SuperCenterGroceryStoreService>();

        await using var root = services.BuildServiceProvider();
        var service = root.GetRequiredService<SuperCenterGroceryStoreService>();

        var result = await service.CheckReceiptAtExitAsync(
            new ReceiptExitCheckRequest(
                "order-200",
                "shopper-200",
                "store-002",
                new DateTime(2026, 3, 31, 12, 0, 0, DateTimeKind.Utc),
                take: 1,
                flagForManualReview: true,
                receiptStatus: SuperCenterReceiptSearch.ClearedStatus),
            CancellationToken.None);

        Assert.True(result.Matched);
        Assert.True(result.FlaggedForManualReview);
        Assert.True(result.InvalidatedTargets > 0);
        Assert.Equal(provider.SearchDocumentKey, provider.LastRemovedKey);

        var after = await service.CheckReceiptAtExitAsync(
            new ReceiptExitCheckRequest(
                "order-200",
                "shopper-200",
                "store-002",
                new DateTime(2026, 3, 31, 12, 0, 0, DateTimeKind.Utc),
                take: 1,
                flagForManualReview: false,
                receiptStatus: SuperCenterReceiptSearch.ClearedStatus),
            CancellationToken.None);

        Assert.False(after.Matched);
    }

    private sealed class RecordingReceiptSearchStore : IRedisHashSearchDocumentStore<ReceiptSearchDocument>
    {
        private readonly ReceiptSearchDocumentMapper _mapper;

        public RecordingReceiptSearchStore(string documentIdPrefix)
        {
            _mapper = new ReceiptSearchDocumentMapper(documentIdPrefix: documentIdPrefix);
        }

        public RedisSearchIndexDefinition Index => _mapper.Index;

        public bool EnsureIndexCalled { get; private set; }
        public ReceiptSearchDocument? LastDocument { get; private set; }
        public string? LastDocumentId { get; private set; }
        public TimeSpan? LastTtl { get; private set; }

        public ValueTask<bool> EnsureIndexAsync(CancellationToken ct = default)
        {
            EnsureIndexCalled = true;
            return ValueTask.FromResult(true);
        }

        public ValueTask<string> UpsertAsync(ReceiptSearchDocument document, TimeSpan? ttl = null, CancellationToken ct = default)
        {
            LastDocument = document;
            LastDocumentId = _mapper.GetDocumentId(document);
            LastTtl = ttl;
            return ValueTask.FromResult(_mapper.Index.GetDocumentKey(LastDocumentId));
        }

        public ValueTask<bool> DeleteAsync(string documentId, CancellationToken ct = default)
            => ValueTask.FromResult(true);

        public ValueTask<string[]> SearchIdsAsync(RedisSearchQuery query, CancellationToken ct = default)
            => ValueTask.FromResult(Array.Empty<string>());

        public ValueTask<long> SearchCountAsync(RedisSearchQuery query, CancellationToken ct = default)
            => ValueTask.FromResult(0L);

        public string GetDocumentKey(string documentId)
            => _mapper.Index.GetDocumentKey(documentId);
    }

    private sealed class ReceiptInvalidationTestProvider : ISuperCenterStoreProvider
    {
        private bool _deleted;

        public ReceiptInvalidationTestProvider(string searchDocumentKey)
        {
            SearchDocumentKey = searchDocumentKey;
        }

        public string SearchDocumentKey { get; }
        public string? LastRemovedKey { get; private set; }

        public ValueTask<Product?> GetProductAsync(string productId, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask CacheProductAsync(Product product, TimeSpan ttl, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<CartItem[]> GetCartAsync(string shopperId, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask SetCartAsync(string shopperId, CartItem[] items, TimeSpan ttl, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<long> GetCartCountAsync(string shopperId, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask RemoveCartAsync(string shopperId, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask JoinFlashSaleAsync(string saleId, string shopperId, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<bool> IsInFlashSaleAsync(string saleId, string shopperId, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<long> GetFlashSaleParticipantCountAsync(string saleId, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask SaveSessionAsync(string sessionId, UserSession session, TimeSpan ttl, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<UserSession?> GetSessionAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask TrackRecentlyViewedAsync(string shopperId, IReadOnlyList<string> productIds, DateTime viewedAtUtc, TimeSpan ttl, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<string[]> GetRecentlyViewedAsync(string shopperId, int take, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask RecordCheckoutEventAsync(GroceryCheckoutEvent checkoutEvent, TimeSpan ttl, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<long> InvalidateTagAsync(string tag, CancellationToken ct) => ValueTask.FromResult(0L);
        public ValueTask<SuperCenterCommandCoverageSnapshot> ExecuteCommandCoverageAsync(SuperCenterCommandCoverageContext context, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<ReceiptSearchLookup> SearchReceiptAsync(ReceiptExitCheckRequest request, CancellationToken ct)
            => ValueTask.FromResult(new ReceiptSearchLookup(!_deleted, _deleted ? 0L : 1L, SearchDocumentKey));

        public ValueTask<bool> RemoveKeyAsync(string key, CancellationToken ct)
        {
            LastRemovedKey = key;
            if (string.Equals(key, SearchDocumentKey, StringComparison.Ordinal))
                _deleted = true;
            return ValueTask.FromResult(true);
        }
    }
}
