using Microsoft.Extensions.Caching.Memory;
using VapeCache.Abstractions.Caching;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Caching.Codecs;
using VapeCache.Tests.Infrastructure;

namespace VapeCache.Tests.Caching;

public sealed class VapeCacheClientTests
{
    [Fact]
    public async Task Set_and_get_round_trip_with_typed_key()
    {
        var client = CreateClient();
        var key = CacheKey<TestPayload>.From("payload:1");

        await client.SetAsync(key, new TestPayload { Id = 1, Name = "alpha" }, new CacheEntryOptions(TimeSpan.FromMinutes(1)));
        var value = await client.GetAsync(key);

        Assert.NotNull(value);
        Assert.Equal(1, value!.Id);
        Assert.Equal("alpha", value.Name);
    }

    [Fact]
    public async Task Region_prefixes_keys_for_get_set_remove()
    {
        var client = CreateClient();
        var region = client.Region("users");

        await region.SetAsync("42", new TestPayload { Id = 42, Name = "neo" }, new CacheEntryOptions(TimeSpan.FromMinutes(1)));

        var viaRegion = await region.GetAsync<TestPayload>("42");
        var viaRawKey = await client.GetAsync(CacheKey<TestPayload>.From("users:42"));

        Assert.NotNull(viaRegion);
        Assert.NotNull(viaRawKey);
        Assert.Equal("neo", viaRegion!.Name);
        Assert.Equal("neo", viaRawKey!.Name);

        var removed = await region.RemoveAsync("42");
        Assert.True(removed);
        Assert.Null(await client.GetAsync(CacheKey<TestPayload>.From("users:42")));
    }

    [Fact]
    public async Task GetOrCreate_invokes_factory_once_for_cached_key()
    {
        var client = CreateClient();
        var key = CacheKey<TestPayload>.From("factory:once");
        var calls = 0;

        async ValueTask<TestPayload> Factory(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            await Task.Yield();
            return new TestPayload { Id = 11, Name = "cached" };
        }

        var first = await client.GetOrCreateAsync(key, Factory, new CacheEntryOptions(TimeSpan.FromMinutes(1)));
        var second = await client.GetOrCreateAsync(key, Factory, new CacheEntryOptions(TimeSpan.FromMinutes(1)));

        Assert.Equal("cached", first.Name);
        Assert.Equal("cached", second.Name);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Region_throws_for_invalid_region_name()
    {
        var client = CreateClient();
        Assert.Throws<ArgumentException>(() => client.Region(" "));
    }

    [Fact]
    public async Task Region_throws_for_invalid_id()
    {
        var client = CreateClient();
        var region = client.Region("valid");

        Assert.Throws<ArgumentException>(() => region.Key<TestPayload>(" "));
        await Assert.ThrowsAsync<ArgumentException>(() => region.RemoveAsync(" ").AsTask());
    }

    private static VapeCacheClient CreateClient()
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var current = new CurrentCacheService();
        var stats = new CacheStatsRegistry();
        var spillOptions = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions { EnableSpillToDisk = false });
        var cache = new InMemoryCacheService(memoryCache, current, stats, spillOptions, new NoopSpillStore());
        return new VapeCacheClient(cache, new SystemTextJsonCodecProvider());
    }

    private sealed record TestPayload
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
    }
}
