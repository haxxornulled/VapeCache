using Microsoft.Extensions.DependencyInjection;
using VapeCache.Abstractions.Caching;
using VapeCache.Application.Abstractions;
using VapeCache.Application.Caching.Invalidation;
using VapeCache.Application.Caching.Invalidation.Commands;
using VapeCache.Application.Caching.Invalidation.Events;
using VapeCache.Features.Invalidation;

namespace VapeCache.Tests.Application;

public sealed class ApplicationInvalidationCommandHandlersTests
{
    [Fact]
    public async Task EntityHandler_Publishes_Generic_EntityInvalidation()
    {
        var cache = new RecordingVapeCache();
        var services = CreateServices(cache, CacheInvalidationProfile.HighTrafficSite);

        await using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<ICommandHandler<InvalidateEntityCacheCommand, CacheInvalidationExecutionResult>>();

        var result = await handler.HandleAsync(new InvalidateEntityCacheCommand(
            EntityName: "order",
            EntityIds: ["42"],
            Zones: ["orders"],
            KeyPrefixes: ["order", "order:summary"],
            Tags: ["customer:cust-007"]));

        Assert.Equal(5, result.InvalidatedTargets);
        Assert.Contains("order:42", cache.InvalidatedTags);
        Assert.Contains("customer:cust-007", cache.InvalidatedTags);
        Assert.Contains("orders", cache.InvalidatedZones);
        Assert.Contains("order:42", cache.RemovedKeys);
        Assert.Contains("order:summary:42", cache.RemovedKeys);
    }

    [Fact]
    public async Task TagHandler_Publishes_Direct_Tag_Invalidation()
    {
        var cache = new RecordingVapeCache();
        var services = CreateServices(cache, CacheInvalidationProfile.SmallWebsite);

        await using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<ICommandHandler<InvalidateCacheTagsCommand, CacheInvalidationExecutionResult>>();

        var result = await handler.HandleAsync(new InvalidateCacheTagsCommand("catalog", "featured"));

        Assert.Equal(2, result.InvalidatedTargets);
        Assert.Contains("catalog", cache.InvalidatedTags);
        Assert.Contains("featured", cache.InvalidatedTags);
    }

    [Fact]
    public async Task ZoneHandler_Publishes_Direct_Zone_Invalidation()
    {
        var cache = new RecordingVapeCache();
        var services = CreateServices(cache, CacheInvalidationProfile.SmallWebsite);

        await using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<ICommandHandler<InvalidateCacheZonesCommand, CacheInvalidationExecutionResult>>();

        var result = await handler.HandleAsync(new InvalidateCacheZonesCommand("orders", "catalog"));

        Assert.Equal(2, result.InvalidatedTargets);
        Assert.Contains("orders", cache.InvalidatedZones);
        Assert.Contains("catalog", cache.InvalidatedZones);
    }

    [Fact]
    public async Task KeyHandler_Publishes_Direct_Key_Invalidation()
    {
        var cache = new RecordingVapeCache();
        var services = CreateServices(cache, CacheInvalidationProfile.SmallWebsite);

        await using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<ICommandHandler<InvalidateCacheKeysCommand, CacheInvalidationExecutionResult>>();

        var result = await handler.HandleAsync(new InvalidateCacheKeysCommand("order:42", "order:summary:42"));

        Assert.Equal(2, result.InvalidatedTargets);
        Assert.Contains("order:42", cache.RemovedKeys);
        Assert.Contains("order:summary:42", cache.RemovedKeys);
    }

    [Fact]
    public async Task Publisher_Abstraction_Uses_Dispatcher()
    {
        var cache = new RecordingVapeCache();
        var services = CreateServices(cache, CacheInvalidationProfile.SmallWebsite);

        await using var provider = services.BuildServiceProvider();
        var publisher = provider.GetRequiredService<ICacheInvalidationEventPublisher>();

        var result = await publisher.PublishAsync(new CacheTagsInvalidatedEvent("catalog"));

        Assert.Equal(1, result.InvalidatedTargets);
        Assert.Contains("catalog", cache.InvalidatedTags);
    }

    private static ServiceCollection CreateServices(RecordingVapeCache cache, CacheInvalidationProfile profile)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IVapeCache>(cache);
        services.AddVapeCacheApplicationInvalidation(options => options.Profile = profile);
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
