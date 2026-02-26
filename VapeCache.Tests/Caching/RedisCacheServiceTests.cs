using System.Text.Json;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Tests.Caching;

public sealed class RedisCacheServiceTests
{
    [Fact]
    public async Task GetAsyncTyped_ReturnsDeserializedValue()
    {
        var redis = new InMemoryCommandExecutor();
        var current = new CurrentCacheService();
        var stats = new CacheStatsRegistry();
        var service = new RedisCacheService(redis, current, stats);

        var key = "typed:get";
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Widget { Id = 7, Name = "typed" });
        await redis.SetAsync(key, payload, ttl: null, ct: default);

        var result = await service.GetAsync(key, static bytes =>
            JsonSerializer.Deserialize<Widget>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web)), default);

        Assert.NotNull(result);
        Assert.Equal(7, result!.Id);
        Assert.Equal("typed", result.Name);
    }

    [Fact]
    public async Task GetAsyncTyped_Miss_ReturnsDefault()
    {
        var redis = new InMemoryCommandExecutor();
        var current = new CurrentCacheService();
        var stats = new CacheStatsRegistry();
        var service = new RedisCacheService(redis, current, stats);

        var result = await service.GetAsync("missing:key", static bytes =>
            JsonSerializer.Deserialize<Widget>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web)), default);

        Assert.Null(result);
    }

    private sealed record Widget
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
    }
}
