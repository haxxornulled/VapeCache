using Autofac;
using VapeCache.Abstractions.Caching;
using VapeCache.Application.Abstractions;
using VapeCache.Application.Caching.Invalidation;
using VapeCache.Application.Caching.Invalidation.Commands;
using VapeCache.Features.Invalidation;

namespace VapeCache.Tests.Application;

public sealed class ApplicationInvalidationAutofacTests
{
    [Fact]
    public async Task Autofac_Registration_Resolves_Generic_Handlers_And_Dispatches()
    {
        var cache = new RecordingVapeCache();

        var builder = new ContainerBuilder();
        builder.RegisterInstance<IVapeCache>(cache);
        builder.AddVapeCacheApplicationInvalidation(options =>
        {
            options.Profile = CacheInvalidationProfile.HighTrafficSite;
        });

        await using var container = builder.Build();
        var handler = container.Resolve<ICommandHandler<InvalidateEntityCacheCommand, CacheInvalidationExecutionResult>>();

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
