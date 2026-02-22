using VapeCache.Console.GroceryStore;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Tests.Console;

public sealed class VapeCacheRawGroceryStoreServiceTests
{
    [Fact]
    public async Task Cart_and_flash_sale_operations_round_trip()
    {
        await using var executor = new InMemoryCommandExecutor();
        var sut = new VapeCacheRawGroceryStoreService(executor);

        var userId = "user-100";
        var item1 = new CartItem("prod-001", "Organic Bananas", 2.99m, 1, DateTime.UtcNow);
        var item2 = new CartItem("prod-006", "Organic Whole Milk", 4.29m, 2, DateTime.UtcNow);

        await sut.AddToCartAsync(userId, item1);
        await sut.AddToCartAsync(userId, item2);

        var count = await sut.GetCartCountAsync(userId);
        var cart = await sut.GetCartAsync(userId);

        Assert.Equal(2, count);
        Assert.Equal(2, cart.Length);

        await sut.JoinFlashSaleAsync("sale-1", userId);
        Assert.True(await sut.IsInFlashSaleAsync("sale-1", userId));
        Assert.Equal(1, await sut.GetFlashSaleParticipantCountAsync("sale-1"));

        await sut.ClearCartAsync(userId);
        Assert.Equal(0, await sut.GetCartCountAsync(userId));
    }

    [Fact]
    public async Task Product_and_session_operations_round_trip()
    {
        await using var executor = new InMemoryCommandExecutor();
        var sut = new VapeCacheRawGroceryStoreService(executor);

        var product = await sut.GetProductAsync("prod-001");
        Assert.NotNull(product);
        Assert.Equal("prod-001", product!.Id);

        var custom = new Product("prod-test", "Bench Product", "Custom", 9.99m, 10, "/img/test.jpg");
        await sut.CacheProductAsync(custom, TimeSpan.FromMinutes(5));
        var cached = await sut.GetProductAsync("prod-test");
        Assert.NotNull(cached);
        Assert.Equal("prod-test", cached!.Id);

        var session = new UserSession("u1", "s1", DateTime.UtcNow, DateTime.UtcNow, Array.Empty<string>(), null);
        await sut.SaveSessionAsync("s1", session);

        var loaded = await sut.GetSessionAsync("s1");
        Assert.NotNull(loaded);
        Assert.Equal("u1", loaded!.UserId);
    }

    [Fact]
    public async Task Batched_cart_operations_use_optimized_round_trip()
    {
        await using var executor = new InMemoryCommandExecutor();
        var sut = new VapeCacheRawGroceryStoreService(executor);

        var userId = "user-batch";
        var items = new[]
        {
            new CartItem("prod-001", "Organic Bananas", 2.99m, 1, DateTime.UtcNow),
            new CartItem("prod-002", "Milk", 4.29m, 2, DateTime.UtcNow),
            new CartItem("prod-003", "Bread", 3.49m, 1, DateTime.UtcNow)
        };

        await sut.AddToCartBatchAsync(userId, items);

        var count = await sut.GetCartCountAsync(userId);
        var cart = await sut.GetCartAsync(userId);

        Assert.Equal(3, count);
        Assert.Equal(3, cart.Length);

        await sut.ClearCartAsync(userId);
        Assert.Equal(0, await sut.GetCartCountAsync(userId));
        Assert.Empty(await sut.GetCartAsync(userId));
    }
}
