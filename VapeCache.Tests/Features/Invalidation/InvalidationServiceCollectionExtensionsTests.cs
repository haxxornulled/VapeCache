using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VapeCache.Abstractions.Caching;
using VapeCache.Features.Invalidation;

namespace VapeCache.Tests.Features.Invalidation;

public sealed class InvalidationServiceCollectionExtensionsTests
{
    [Fact]
    public async Task AddSmallWebsiteEntityInvalidationPolicy_DispatchesTagAndZoneTargets()
    {
        var cache = new RecordingVapeCache();
        var services = CreateServices(cache);
        services.AddSmallWebsiteEntityInvalidationPolicy<OrderUpdatedEvent>(
            entityName: "order",
            idsSelector: static e => [e.OrderId],
            zonesSelector: static e => [e.Zone]);

        await using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<ICacheInvalidationDispatcher>();

        var result = await dispatcher.DispatchAsync(new OrderUpdatedEvent("42", "orders"));

        Assert.Equal(2, result.RequestedTargets);
        Assert.Equal(2, result.InvalidatedTargets);
        Assert.Contains("order:42", cache.InvalidatedTags);
        Assert.Contains("orders", cache.InvalidatedZones);
        Assert.Empty(cache.RemovedKeys);
    }

    [Fact]
    public async Task AddHighTrafficEntityInvalidationPolicy_UsesDefaultEntityPrefixForKeys()
    {
        var cache = new RecordingVapeCache();
        var services = CreateServices(cache);
        services.AddHighTrafficEntityInvalidationPolicy<OrderUpdatedEvent>(
            entityName: "order",
            idsSelector: static e => [e.OrderId],
            zonesSelector: static e => [e.Zone]);

        await using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<ICacheInvalidationDispatcher>();

        var result = await dispatcher.DispatchAsync(new OrderUpdatedEvent("42", "orders"));

        Assert.Equal(3, result.RequestedTargets);
        Assert.Equal(3, result.InvalidatedTargets);
        Assert.Contains("order:42", cache.InvalidatedTags);
        Assert.Contains("orders", cache.InvalidatedZones);
        Assert.Contains("order:42", cache.RemovedKeys);
    }

    [Fact]
    public async Task AddDesktopKeyInvalidationPolicy_DispatchesOnlyKeyTargets()
    {
        var cache = new RecordingVapeCache();
        var services = CreateServices(cache);
        services.AddDesktopKeyInvalidationPolicy<OrderUpdatedEvent>(
            keysSelector: static e => [$"order:{e.OrderId}", $"order:summary:{e.OrderId}"]);

        await using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<ICacheInvalidationDispatcher>();

        var result = await dispatcher.DispatchAsync(new OrderUpdatedEvent("42", "orders"));

        Assert.Equal(2, result.RequestedTargets);
        Assert.Equal(2, result.InvalidatedTargets);
        Assert.Empty(cache.InvalidatedTags);
        Assert.Empty(cache.InvalidatedZones);
        Assert.Contains("order:42", cache.RemovedKeys);
        Assert.Contains("order:summary:42", cache.RemovedKeys);
    }

    [Fact]
    public async Task AddVapeCacheInvalidation_DispatchesWithBuiltInEntityPolicy()
    {
        var cache = new RecordingVapeCache();
        var services = CreateServices(cache);
        services.AddEntityInvalidationPolicy<OrderUpdatedEvent>(
            entityName: "order",
            idsSelector: static e => [e.OrderId],
            zonesSelector: static e => [e.Zone],
            keyPrefixes: ["order", "order:summary"]);

        await using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<ICacheInvalidationDispatcher>();

        var result = await dispatcher.DispatchAsync(new OrderUpdatedEvent("42", "orders"));

        Assert.Equal(4, result.RequestedTargets);
        Assert.Equal(4, result.InvalidatedTargets);
        Assert.Contains("order:42", cache.InvalidatedTags);
        Assert.Contains("orders", cache.InvalidatedZones);
        Assert.Contains("order:42", cache.RemovedKeys);
        Assert.Contains("order:summary:42", cache.RemovedKeys);
    }

    private static ServiceCollection CreateServices(RecordingVapeCache cache)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["VapeCache:Invalidation:Enabled"] = "true",
                ["VapeCache:Invalidation:Profile"] = nameof(CacheInvalidationProfile.SmallWebsite)
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IVapeCache>(cache);
        services.AddVapeCacheInvalidation(config);
        return services;
    }

    private sealed record OrderUpdatedEvent(string OrderId, string Zone);

    private sealed class RecordingVapeCache : IVapeCache
    {
        private readonly System.Threading.Lock _gate = new();

        public List<string> InvalidatedTags { get; } = [];

        public List<string> InvalidatedZones { get; } = [];

        public List<string> RemovedKeys { get; } = [];

        public ICacheRegion Region(string name) => throw new NotSupportedException();

        public ValueTask<T?> GetAsync<T>(CacheKey<T> key, CancellationToken ct = default)
            => ValueTask.FromResult<T?>(default);

        public ValueTask SetAsync<T>(CacheKey<T> key, T value, CacheEntryOptions options = default, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask<T> GetOrCreateAsync<T>(
            CacheKey<T> key,
            Func<CancellationToken, ValueTask<T>> factory,
            CacheEntryOptions options = default,
            CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask<bool> RemoveAsync(CacheKey key, CancellationToken ct = default)
        {
            lock (_gate)
                RemovedKeys.Add(key.Value);
            return ValueTask.FromResult(true);
        }

        public ValueTask<long> InvalidateTagAsync(string tag, CancellationToken ct = default)
        {
            lock (_gate)
                InvalidatedTags.Add(tag);
            return ValueTask.FromResult(1L);
        }

        public ValueTask<long> GetTagVersionAsync(string tag, CancellationToken ct = default)
            => ValueTask.FromResult(1L);

        public ValueTask<long> InvalidateZoneAsync(string zone, CancellationToken ct = default)
        {
            lock (_gate)
                InvalidatedZones.Add(zone);
            return ValueTask.FromResult(1L);
        }

        public ValueTask<long> GetZoneVersionAsync(string zone, CancellationToken ct = default)
            => ValueTask.FromResult(1L);
    }
}
