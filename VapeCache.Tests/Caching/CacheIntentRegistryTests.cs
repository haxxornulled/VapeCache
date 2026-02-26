using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Infrastructure.Caching;

namespace VapeCache.Tests.Caching;

public sealed class CacheIntentRegistryTests
{
    [Fact]
    public async Task InMemoryCacheService_RecordsIntent_OnSet_AndRemovesOnDelete()
    {
        var memory = new MemoryCache(new MemoryCacheOptions());
        var current = new CurrentCacheService();
        var stats = new CacheStatsRegistry();
        var spillOptions = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions());
        var intentRegistry = new CacheIntentRegistry();
        var cache = new InMemoryCacheService(memory, current, stats, spillOptions, new NoopSpillStore(), intentRegistry);

        var intent = new CacheIntent(CacheIntentKind.QueryResult, "catalog-page", "api");
        var options = new CacheEntryOptions(TimeSpan.FromMinutes(5), intent);
        await cache.SetAsync("intent:key", "payload"u8.ToArray(), options, CancellationToken.None);

        Assert.True(intentRegistry.TryGet("intent:key", out var entry));
        Assert.NotNull(entry);
        Assert.Equal("memory", entry!.Backend);
        Assert.Equal(CacheIntentKind.QueryResult, entry.Intent.Kind);
        Assert.Equal("catalog-page", entry.Intent.Reason);

        await cache.RemoveAsync("intent:key", CancellationToken.None);
        Assert.False(intentRegistry.TryGet("intent:key", out _));
    }

    [Fact]
    public void CacheIntentRegistry_PrunesExpiredEntries()
    {
        var registry = new CacheIntentRegistry();
        var expired = new CacheEntryOptions(TimeSpan.FromMilliseconds(1), new CacheIntent(CacheIntentKind.ReadThrough, "short"));
        var active = new CacheEntryOptions(TimeSpan.FromMinutes(1), new CacheIntent(CacheIntentKind.ReadThrough, "active"));

        registry.RecordSet("expired:key", "memory", expired, 10);
        Thread.Sleep(20);
        registry.RecordSet("active:key", "memory", active, 20);

        Assert.False(registry.TryGet("expired:key", out _));
        Assert.True(registry.TryGet("active:key", out _));

        var recent = registry.GetRecent(10);
        Assert.DoesNotContain(recent, static entry => entry.Key == "expired:key");
        Assert.Contains(recent, static entry => entry.Key == "active:key");
    }

    private sealed class TestOptionsMonitor<T>(T current) : IOptionsMonitor<T>
    {
        public T CurrentValue => current;
        public T Get(string? name) => current;
        public IDisposable OnChange(Action<T, string?> listener) => NoopDisposable.Instance;
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}
