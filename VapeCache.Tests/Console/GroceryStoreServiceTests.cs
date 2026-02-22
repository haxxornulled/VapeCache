using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using VapeCache.Abstractions.Caching;
using VapeCache.Console.GroceryStore;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Caching.Codecs;
using VapeCache.Infrastructure.Collections;
using VapeCache.Infrastructure.Connections;
using VapeCache.Tests.Infrastructure;

namespace VapeCache.Tests.Console;

public sealed class GroceryStoreServiceTests
{
    [Fact]
    public async Task Cart_operations_round_trip()
    {
        await using var h = GroceryHarness.Create();

        var userId = "user-1";
        var apples = new CartItem("prod-001", "Organic Bananas", 2.99m, 1, DateTime.UtcNow);
        var milk = new CartItem("prod-006", "Organic Whole Milk", 4.29m, 2, DateTime.UtcNow);

        await h.Service.AddToCartAsync(userId, apples);
        await h.Service.AddToCartAsync(userId, milk);

        Assert.Equal(2, await h.Service.GetCartCountAsync(userId));

        var cart = await h.Service.GetCartAsync(userId);
        Assert.Equal(2, cart.Length);

        var removed = await h.Service.RemoveFromCartAsync(userId);
        Assert.NotNull(removed);
        Assert.Equal(apples.ProductId, removed!.ProductId);

        await h.Service.ClearCartAsync(userId);
        Assert.Equal(0, await h.Service.GetCartCountAsync(userId));
    }

    [Fact]
    public async Task Flash_sale_membership_is_idempotent()
    {
        await using var h = GroceryHarness.Create();

        const string saleId = "sale-abc";
        const string userId = "user-123";

        await h.Service.JoinFlashSaleAsync(saleId, userId);
        await h.Service.JoinFlashSaleAsync(saleId, userId);

        Assert.True(await h.Service.IsInFlashSaleAsync(saleId, userId));
        Assert.Equal(1, await h.Service.GetFlashSaleParticipantCountAsync(saleId));

        var participants = await h.Service.GetFlashSaleParticipantsAsync(saleId);
        Assert.Single(participants);
        Assert.Equal(userId, participants[0]);

        Assert.True(await h.Service.LeaveFlashSaleAsync(saleId, userId));
        Assert.False(await h.Service.LeaveFlashSaleAsync(saleId, userId));
    }

    [Fact]
    public async Task Session_storage_supports_single_and_batch_reads()
    {
        await using var h = GroceryHarness.Create();

        var s1 = new UserSession("u1", "s1", DateTime.UtcNow, DateTime.UtcNow, Array.Empty<string>(), null);
        var s2 = new UserSession("u2", "s2", DateTime.UtcNow, DateTime.UtcNow, new[] { "prod-001" }, "c2");

        await h.Service.SaveSessionAsync("s1", s1);
        await h.Service.SaveSessionAsync("s2", s2);

        var single = await h.Service.GetSessionAsync("s1");
        Assert.NotNull(single);
        Assert.Equal("u1", single!.UserId);

        var many = await h.Service.GetSessionsAsync("s1", "s2", "missing");
        Assert.Equal(3, many.Length);
        Assert.NotNull(many[0]);
        Assert.NotNull(many[1]);
        Assert.Null(many[2]);
    }

    [Fact]
    public async Task Product_lookup_and_inventory_update_track_history()
    {
        await using var h = GroceryHarness.Create();

        var product = await h.Service.GetProductAsync("prod-001");
        Assert.NotNull(product);
        Assert.Equal("prod-001", product!.Id);

        await h.Service.UpdateInventoryAsync("prod-001", 123);

        var updated = await h.Service.GetProductAsync("prod-001");
        Assert.NotNull(updated);
        Assert.Equal(123, updated!.StockQuantity);

        var history = await h.Service.GetInventoryHistoryAsync("prod-001", limit: 5);
        Assert.NotEmpty(history);
        Assert.Equal(123, history[0].NewQuantity);

        var missing = await h.Service.GetProductAsync("prod-missing");
        Assert.Null(missing);
    }

    [Fact]
    public async Task Flash_sale_create_and_read_back()
    {
        await using var h = GroceryHarness.Create();

        var sale = await h.Service.CreateFlashSaleAsync("prod-002", 1.99m, 50, TimeSpan.FromMinutes(2));
        var cached = await h.Service.GetFlashSaleAsync(sale.Id);

        Assert.NotNull(cached);
        Assert.Equal(sale.Id, cached!.Id);
        Assert.Equal("prod-002", cached.ProductId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Service.CreateFlashSaleAsync("prod-does-not-exist", 1.0m, 5, TimeSpan.FromMinutes(1)));
    }

    private sealed class GroceryHarness : IAsyncDisposable
    {
        private readonly InMemoryCommandExecutor _executor;
        private readonly MemoryCache _memoryCache;

        public GroceryStoreService Service { get; }

        private GroceryHarness(GroceryStoreService service, InMemoryCommandExecutor executor, MemoryCache memoryCache)
        {
            Service = service;
            _executor = executor;
            _memoryCache = memoryCache;
        }

        public static GroceryHarness Create()
        {
            var executor = new InMemoryCommandExecutor();
            var codecs = new SystemTextJsonCodecProvider();
            var collections = new CacheCollectionFactory(executor, codecs);

            var current = new CurrentCacheService();
            var stats = new CacheStatsRegistry();
            var memory = new MemoryCache(new MemoryCacheOptions());
            var spillOptions = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions { EnableSpillToDisk = false });
            var cacheService = new InMemoryCacheService(memory, current, stats, spillOptions, new NoopSpillStore());
            var client = new VapeCacheClient(cacheService, codecs);

            var service = new GroceryStoreService(collections, client, NullLogger<GroceryStoreService>.Instance);
            return new GroceryHarness(service, executor, memory);
        }

        public async ValueTask DisposeAsync()
        {
            _memoryCache.Dispose();
            await _executor.DisposeAsync();
        }
    }
}
