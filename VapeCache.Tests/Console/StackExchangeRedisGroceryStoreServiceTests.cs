using System.Text.Json;
using Moq;
using StackExchange.Redis;
using VapeCache.Console.GroceryStore;

namespace VapeCache.Tests.Console;

public sealed class StackExchangeRedisGroceryStoreServiceTests
{
    [Fact]
    public async Task Product_and_session_methods_round_trip_json()
    {
        var db = new Mock<IDatabase>(MockBehavior.Loose);
        var mux = new Mock<IConnectionMultiplexer>(MockBehavior.Loose);
        mux.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(db.Object);

        var product = new Product("prod-001", "Bananas", "Produce", 2.99m, 10, "/img");
        var productJson = JsonSerializer.Serialize(product);
        db.Setup(d => d.StringGetAsync("product:prod-001", It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisValue)productJson);
        db.Setup(d => d.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        var session = new UserSession("u1", "s1", DateTime.UtcNow, DateTime.UtcNow, Array.Empty<string>(), null);
        var sessionJson = JsonSerializer.Serialize(session);
        db.Setup(d => d.StringGetAsync("session:s1", It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisValue)sessionJson);

        var sut = new StackExchangeRedisGroceryStoreService(mux.Object);

        await sut.CacheProductAsync(product, TimeSpan.FromMinutes(1));
        var loadedProduct = await sut.GetProductAsync("prod-001");
        Assert.NotNull(loadedProduct);
        Assert.Equal(product.Id, loadedProduct!.Id);

        await sut.SaveSessionAsync("s1", session);
        var loadedSession = await sut.GetSessionAsync("s1");
        Assert.NotNull(loadedSession);
        Assert.Equal(session.UserId, loadedSession!.UserId);
    }

    [Fact]
    public async Task List_and_set_methods_delegate_to_database()
    {
        var db = new Mock<IDatabase>(MockBehavior.Loose);
        var mux = new Mock<IConnectionMultiplexer>(MockBehavior.Loose);
        mux.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(db.Object);

        db.Setup(d => d.ListRightPushAsync(
                "cart:user-1",
                It.IsAny<RedisValue>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(1L);
        db.Setup(d => d.ListRangeAsync("cart:user-1", It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(new RedisValue[]
            {
                JsonSerializer.Serialize(new CartItem("p1", "Milk", 3.99m, 2, DateTime.UtcNow))
            });
        db.Setup(d => d.ListLengthAsync("cart:user-1", It.IsAny<CommandFlags>())).ReturnsAsync(1L);
        db.Setup(d => d.KeyDeleteAsync("cart:user-1", It.IsAny<CommandFlags>())).ReturnsAsync(true);

        db.Setup(d => d.SetAddAsync("sale:s1:participants", "user-1", It.IsAny<CommandFlags>())).ReturnsAsync(true);
        db.Setup(d => d.SetContainsAsync("sale:s1:participants", "user-1", It.IsAny<CommandFlags>())).ReturnsAsync(true);
        db.Setup(d => d.SetLengthAsync("sale:s1:participants", It.IsAny<CommandFlags>())).ReturnsAsync(1L);

        var sut = new StackExchangeRedisGroceryStoreService(mux.Object);

        await sut.AddToCartAsync("user-1", new CartItem("p1", "Milk", 3.99m, 2, DateTime.UtcNow));
        var cart = await sut.GetCartAsync("user-1");
        var count = await sut.GetCartCountAsync("user-1");
        await sut.ClearCartAsync("user-1");

        await sut.JoinFlashSaleAsync("s1", "user-1");
        var inSale = await sut.IsInFlashSaleAsync("s1", "user-1");
        var participants = await sut.GetFlashSaleParticipantCountAsync("s1");

        Assert.Single(cart);
        Assert.Equal(1, count);
        Assert.True(inSale);
        Assert.Equal(1, participants);
    }
}
