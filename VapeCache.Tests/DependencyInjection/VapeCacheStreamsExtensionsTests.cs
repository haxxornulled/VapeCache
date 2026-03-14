using Moq;
using Microsoft.Extensions.DependencyInjection;
using VapeCache.Abstractions.Connections;
using VapeCache.Extensions.Streams;

namespace VapeCache.Tests.DependencyInjection;

public sealed class VapeCacheStreamsExtensionsTests
{
    [Fact]
    public async Task AddVapeCacheStreams_RegistersProducer_AndBindsOptions()
    {
        var redis = new Mock<IRedisCommandExecutor>(MockBehavior.Strict);
        redis.Setup(x => x.XAddIdempotentAsync(
                "stream:orders",
                "orders-api",
                "tx-1001",
                false,
                "*",
                It.IsAny<(string Field, ReadOnlyMemory<byte> Value)[]>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("1741350000000-0");

        redis.Setup(x => x.XCfgSetIdempotenceAsync("stream:orders", 600, 256, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var services = new ServiceCollection();
        services.AddSingleton(redis.Object);
        services.AddVapeCacheStreams(options =>
        {
            options.DefaultEntryId = "*";
            options.UseAutoIdempotentId = false;
        });

        await using var provider = services.BuildServiceProvider();
        var producer = provider.GetRequiredService<IRedisStreamIdempotentProducer>();

        var id = await producer.PublishAsync(
            key: "stream:orders",
            producerId: "orders-api",
            idempotentId: "tx-1001",
            fields: [("orderId", (ReadOnlyMemory<byte>)"1001"u8.ToArray())],
            ct: default);

        var configured = await producer.ConfigureIdempotenceAsync(
            key: "stream:orders",
            durationSeconds: 600,
            maxSize: 256,
            ct: default);

        Assert.Equal("1741350000000-0", id);
        Assert.True(configured);
        redis.VerifyAll();
    }

    [Fact]
    public async Task Producer_UsesIdmpAuto_WhenConfigured()
    {
        var redis = new Mock<IRedisCommandExecutor>(MockBehavior.Strict);
        redis.Setup(x => x.XAddIdempotentAsync(
                "stream:events",
                "events-api",
                null,
                true,
                "*",
                It.IsAny<(string Field, ReadOnlyMemory<byte> Value)[]>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("1741350000001-0");

        var services = new ServiceCollection();
        services.AddSingleton(redis.Object);
        services.AddVapeCacheStreams(options =>
        {
            options.DefaultEntryId = "*";
            options.UseAutoIdempotentId = true;
        });

        await using var provider = services.BuildServiceProvider();
        var producer = provider.GetRequiredService<IRedisStreamIdempotentProducer>();
        var id = await producer.PublishAsync(
            key: "stream:events",
            producerId: "events-api",
            idempotentId: null,
            fields: [("name", (ReadOnlyMemory<byte>)"created"u8.ToArray())],
            ct: default);

        Assert.Equal("1741350000001-0", id);
        redis.VerifyAll();
    }
}
