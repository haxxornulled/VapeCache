using VapeCache.Console.GroceryStore;
using VapeCache.Infrastructure.Connections;
using System.Text.Json;

namespace VapeCache.Tests.Console;

public sealed class VapeCacheRawParityGroceryStoreServiceTests
{
    [Fact]
    public async Task Cart_and_flash_sale_operations_round_trip()
    {
        await using var executor = new InMemoryCommandExecutor();
        var sut = new VapeCacheRawParityGroceryStoreService(executor);

        var userId = "user-parity";
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
    public async Task Session_operations_round_trip()
    {
        await using var executor = new InMemoryCommandExecutor();
        var sut = new VapeCacheRawParityGroceryStoreService(executor);
        var session = new UserSession("user-parity", "session-parity", DateTime.UtcNow, DateTime.UtcNow, ["prod-001"], "cart-1");

        await sut.SaveSessionAsync("session-parity", session);
        var loaded = await sut.GetSessionAsync("session-parity");

        Assert.NotNull(loaded);
        Assert.Equal(session.UserId, loaded!.UserId);
        Assert.Equal(session.SessionId, loaded.SessionId);
        Assert.Equal(session.ActiveCartId, loaded.ActiveCartId);
        Assert.Equal(session.RecentlyViewedProductIds, loaded.RecentlyViewedProductIds);
    }

    [Fact]
    public async Task AddToCartBatchAsync_stores_entries_in_list_shape_only()
    {
        await using var executor = new InMemoryCommandExecutor();
        var sut = new VapeCacheRawParityGroceryStoreService(executor);
        var userId = "batch-user";
        var now = DateTime.UnixEpoch;
        var items = new[]
        {
            new CartItem("prod-001", "Organic Bananas", 2.99m, 1, now),
            new CartItem("prod-006", "Organic Whole Milk", 4.29m, 2, now)
        };

        await sut.AddToCartBatchAsync(userId, items);

        var count = await sut.GetCartCountAsync(userId);
        var cart = await sut.GetCartAsync(userId);
        using var optimizedCartLease = await executor.GetLeaseAsync($"cart:optimized:{userId}", CancellationToken.None);
        using var optimizedCountLease = await executor.GetLeaseAsync($"cart:optimized:{userId}:count", CancellationToken.None);

        Assert.Equal(items.Length, count);
        Assert.Equal(items.Length, cart.Length);
        Assert.True(optimizedCartLease.IsNull);
        Assert.True(optimizedCountLease.IsNull);
    }

    [Fact]
    public async Task GetSessionAsync_supports_legacy_json_payload()
    {
        await using var executor = new InMemoryCommandExecutor();
        var sut = new VapeCacheRawParityGroceryStoreService(executor);
        var session = new UserSession("legacy-user", "legacy-session", DateTime.UtcNow, DateTime.UtcNow, ["prod-001"], "cart-1");
        var json = JsonSerializer.SerializeToUtf8Bytes(session, new GroceryStoreJsonContext(new()).UserSession);

        await executor.SetAsync("session:legacy-session", json, TimeSpan.FromHours(1), CancellationToken.None);

        var loaded = await sut.GetSessionAsync("legacy-session");

        Assert.NotNull(loaded);
        Assert.Equal(session.UserId, loaded!.UserId);
        Assert.Equal(session.SessionId, loaded.SessionId);
        Assert.Equal(session.ActiveCartId, loaded.ActiveCartId);
        Assert.Equal(session.RecentlyViewedProductIds, loaded.RecentlyViewedProductIds);
    }
}
