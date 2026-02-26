using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Extensions.AspNetCore;

namespace VapeCache.Tests.AspNetCore;

public sealed class VapeCacheOutputCacheStoreTests
{
    [Fact]
    public async Task SetAndGetAsync_UsesConfiguredPrefix()
    {
        var cache = new InMemoryRawCacheService();
        var options = new StaticOptionsMonitor<VapeCacheOutputCacheStoreOptions>(new VapeCacheOutputCacheStoreOptions
        {
            KeyPrefix = "test:output"
        });
        var store = new VapeCacheOutputCacheStore(cache, options, NullLogger<VapeCacheOutputCacheStore>.Instance);

        var payload = new byte[] { 0x01, 0x02, 0x03 };
        await store.SetAsync("abc", payload, tags: null, TimeSpan.FromSeconds(30), CancellationToken.None);

        var roundtrip = await store.GetAsync("abc", CancellationToken.None);
        Assert.NotNull(roundtrip);
        Assert.Equal(payload, roundtrip);
        Assert.True(cache.Exists("test:output:abc"));
    }

    [Fact]
    public async Task EvictByTagAsync_RemovesTaggedEntries()
    {
        var cache = new InMemoryRawCacheService();
        var options = new StaticOptionsMonitor<VapeCacheOutputCacheStoreOptions>(new VapeCacheOutputCacheStoreOptions
        {
            KeyPrefix = "test:output",
            EnableTagIndexing = true
        });
        var store = new VapeCacheOutputCacheStore(cache, options, NullLogger<VapeCacheOutputCacheStore>.Instance);

        await store.SetAsync("key-a", new byte[] { 0x01 }, new[] { "home" }, TimeSpan.FromSeconds(30), CancellationToken.None);
        await store.SetAsync("key-b", new byte[] { 0x02 }, new[] { "home" }, TimeSpan.FromSeconds(30), CancellationToken.None);
        await store.SetAsync("key-c", new byte[] { 0x03 }, new[] { "other" }, TimeSpan.FromSeconds(30), CancellationToken.None);

        await store.EvictByTagAsync("home", CancellationToken.None);

        Assert.Null(await store.GetAsync("key-a", CancellationToken.None));
        Assert.Null(await store.GetAsync("key-b", CancellationToken.None));
        Assert.NotNull(await store.GetAsync("key-c", CancellationToken.None));
    }

    [Fact]
    public void AddVapeCacheOutputCaching_BindsStoreOptionsFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["VapeCacheOutputCache:KeyPrefix"] = "cfg:output",
                ["VapeCacheOutputCache:DefaultTtl"] = "00:00:45",
                ["VapeCacheOutputCache:EnableTagIndexing"] = "false"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddVapeCacheOutputCaching(configuration);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<VapeCacheOutputCacheStoreOptions>>().CurrentValue;

        Assert.Equal("cfg:output", options.KeyPrefix);
        Assert.Equal(TimeSpan.FromSeconds(45), options.DefaultTtl);
        Assert.False(options.EnableTagIndexing);
    }

    private sealed class InMemoryRawCacheService : ICacheService
    {
        private readonly Dictionary<string, byte[]> _store = new(StringComparer.Ordinal);
        private readonly Lock _gate = new();

        public string Name => "in-memory-raw";

        public ValueTask<byte[]?> GetAsync(string key, CancellationToken ct)
        {
            lock (_gate)
            {
                return ValueTask.FromResult(_store.TryGetValue(key, out var payload) ? payload : null);
            }
        }

        public ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CacheEntryOptions options, CancellationToken ct)
        {
            lock (_gate)
            {
                _store[key] = value.ToArray();
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> RemoveAsync(string key, CancellationToken ct)
        {
            lock (_gate)
            {
                return ValueTask.FromResult(_store.Remove(key));
            }
        }

        public ValueTask<T?> GetAsync<T>(string key, SpanDeserializer<T> deserialize, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask SetAsync<T>(string key, T value, Action<System.Buffers.IBufferWriter<byte>, T> serialize, CacheEntryOptions options, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<T> GetOrSetAsync<T>(string key, Func<CancellationToken, ValueTask<T>> factory, Action<System.Buffers.IBufferWriter<byte>, T> serialize, SpanDeserializer<T> deserialize, CacheEntryOptions options, CancellationToken ct)
            => throw new NotSupportedException();

        public bool Exists(string key)
        {
            lock (_gate)
            {
                return _store.ContainsKey(key);
            }
        }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable OnChange(Action<T, string?> listener) => NoopDisposable.Instance;
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose()
        {
        }
    }
}
