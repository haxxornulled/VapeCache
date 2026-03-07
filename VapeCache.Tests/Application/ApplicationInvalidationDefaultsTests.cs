using Microsoft.Extensions.DependencyInjection;
using VapeCache.Abstractions.Caching;
using VapeCache.Application.Caching.Invalidation;
using VapeCache.Application.Caching.Invalidation.Events;
using VapeCache.Features.Invalidation;

namespace VapeCache.Tests.Application;

public sealed class ApplicationInvalidationDefaultsTests
{
    [Fact]
    public async Task SmallWebsite_Profile_EntityEvent_InvalidatesTagsAndZones_WithoutKeys()
    {
        var cache = new RecordingVapeCache();
        var services = CreateServices(
            cache,
            options => options.Profile = CacheInvalidationProfile.SmallWebsite);

        await using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<ICacheInvalidationDispatcher>();

        var result = await dispatcher.DispatchAsync(new EntityCacheChangedEvent(
            "order",
            ["42"],
            ["orders"],
            ["order", "order:summary"]));

        Assert.Equal(2, result.RequestedTargets);
        Assert.Equal(2, result.InvalidatedTargets);
        Assert.Contains("order:42", cache.InvalidatedTags);
        Assert.Contains("orders", cache.InvalidatedZones);
        Assert.Empty(cache.RemovedKeys);
    }

    [Fact]
    public async Task HighTraffic_Profile_EntityEvent_InvalidatesTagsZonesAndKeys()
    {
        var cache = new RecordingVapeCache();
        var services = CreateServices(
            cache,
            options => options.Profile = CacheInvalidationProfile.HighTrafficSite);

        await using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<ICacheInvalidationDispatcher>();

        var result = await dispatcher.DispatchAsync(new EntityCacheChangedEvent(
            "order",
            ["42"],
            ["orders"],
            ["order", "order:summary"]));

        Assert.Equal(4, result.RequestedTargets);
        Assert.Equal(4, result.InvalidatedTargets);
        Assert.Contains("order:42", cache.InvalidatedTags);
        Assert.Contains("orders", cache.InvalidatedZones);
        Assert.Contains("order:42", cache.RemovedKeys);
        Assert.Contains("order:summary:42", cache.RemovedKeys);
    }

    [Fact]
    public async Task Desktop_Profile_EntityEvent_InvalidatesKeysOnly()
    {
        var cache = new RecordingVapeCache();
        var services = CreateServices(
            cache,
            options => options.Profile = CacheInvalidationProfile.DesktopApp);

        await using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<ICacheInvalidationDispatcher>();

        var result = await dispatcher.DispatchAsync(new EntityCacheChangedEvent(
            "order",
            ["42"],
            ["orders"],
            ["order", "order:summary"]));

        Assert.Equal(2, result.RequestedTargets);
        Assert.Equal(2, result.InvalidatedTargets);
        Assert.Empty(cache.InvalidatedTags);
        Assert.Empty(cache.InvalidatedZones);
        Assert.Contains("order:42", cache.RemovedKeys);
        Assert.Contains("order:summary:42", cache.RemovedKeys);
    }

    [Fact]
    public async Task Explicit_TargetEvents_Are_Dispatched_By_Defaults()
    {
        var cache = new RecordingVapeCache();
        var services = CreateServices(
            cache,
            options => options.Profile = CacheInvalidationProfile.SmallWebsite);

        await using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<ICacheInvalidationDispatcher>();

        _ = await dispatcher.DispatchAsync(new CacheTagsInvalidatedEvent("catalog"));
        _ = await dispatcher.DispatchAsync(new CacheZonesInvalidatedEvent("products"));
        _ = await dispatcher.DispatchAsync(new CacheKeysInvalidatedEvent("product:42"));

        Assert.Contains("catalog", cache.InvalidatedTags);
        Assert.Contains("products", cache.InvalidatedZones);
        Assert.Contains("product:42", cache.RemovedKeys);
    }

    private static ServiceCollection CreateServices(
        RecordingVapeCache cache,
        Action<CacheInvalidationOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IVapeCache>(cache);
        services.AddVapeCacheApplicationInvalidation(configure);
        return services;
    }

    private sealed class RecordingVapeCache : IVapeCache
    {
        private readonly System.Threading.Lock _gate = new();

        public List<string> InvalidatedTags { get; } = [];

        public List<string> InvalidatedZones { get; } = [];

        public List<string> RemovedKeys { get; } = [];

        public ICacheRegion Region(string name) => throw new NotSupportedException();

        public ValueTask<T?> GetAsync<T>(CacheKey<T> key, CancellationToken ct = default)
            => ValueTask.FromResult<T?>(default);

        public ValueTask SetAsync<T>(
            CacheKey<T> key,
            T value,
            CacheEntryOptions options = default,
            CancellationToken ct = default)
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
