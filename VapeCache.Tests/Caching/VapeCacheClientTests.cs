using Microsoft.Extensions.Caching.Memory;
using VapeCache.Abstractions.Caching;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Caching.Codecs;
using VapeCache.Tests.Infrastructure;
using System.Buffers;

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

    [Fact]
    public async Task Tag_and_zone_operations_delegate_to_backend_service()
    {
        var backend = new FakeTaggableCacheService();
        var client = new VapeCacheClient(backend, new SystemTextJsonCodecProvider());

        Assert.Equal(1L, await client.InvalidateTagAsync("catalog"));
        Assert.Equal(1L, await client.GetTagVersionAsync("catalog"));
        Assert.Equal(1L, await client.InvalidateZoneAsync("ef:products"));
        Assert.Equal(1L, await client.GetZoneVersionAsync("ef:products"));
    }

    [Fact]
    public async Task Tag_operations_throw_when_backend_does_not_support_tags()
    {
        var client = CreateClient();

        await Assert.ThrowsAsync<NotSupportedException>(() => client.InvalidateTagAsync("catalog").AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(() => client.InvalidateZoneAsync("ef:products").AsTask());
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

    private sealed class FakeTaggableCacheService : ICacheService, ICacheTagService
    {
        private readonly Dictionary<string, long> _versions = new(StringComparer.Ordinal);

        public string Name => "fake-tags";

        public ValueTask<long> InvalidateTagAsync(string tag, CancellationToken ct = default)
        {
            var normalized = tag.Trim();
            var current = _versions.TryGetValue(normalized, out var version) ? version : 0;
            var next = current + 1;
            _versions[normalized] = next;
            return ValueTask.FromResult(next);
        }

        public ValueTask<long> GetTagVersionAsync(string tag, CancellationToken ct = default)
        {
            var normalized = tag.Trim();
            return ValueTask.FromResult(_versions.TryGetValue(normalized, out var version) ? version : 0L);
        }

        public ValueTask<long> InvalidateZoneAsync(string zone, CancellationToken ct = default)
            => InvalidateTagAsync(CacheTagConventions.ToZoneTag(zone), ct);

        public ValueTask<long> GetZoneVersionAsync(string zone, CancellationToken ct = default)
            => GetTagVersionAsync(CacheTagConventions.ToZoneTag(zone), ct);

        public ValueTask<byte[]?> GetAsync(string key, CancellationToken ct)
            => ValueTask.FromResult<byte[]?>(null);

        public ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CacheEntryOptions options, CancellationToken ct)
            => ValueTask.CompletedTask;

        public ValueTask<bool> RemoveAsync(string key, CancellationToken ct)
            => ValueTask.FromResult(false);

        public ValueTask<T?> GetAsync<T>(string key, SpanDeserializer<T> deserialize, CancellationToken ct)
            => ValueTask.FromResult<T?>(default);

        public ValueTask SetAsync<T>(string key, T value, Action<IBufferWriter<byte>, T> serialize, CacheEntryOptions options, CancellationToken ct)
            => ValueTask.CompletedTask;

        public async ValueTask<T> GetOrSetAsync<T>(
            string key,
            Func<CancellationToken, ValueTask<T>> factory,
            Action<IBufferWriter<byte>, T> serialize,
            SpanDeserializer<T> deserialize,
            CacheEntryOptions options,
            CancellationToken ct)
            => await factory(ct).ConfigureAwait(false);
    }
}
