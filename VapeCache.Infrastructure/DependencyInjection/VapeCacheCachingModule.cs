using Autofac;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Collections;
using VapeCache.Abstractions.Connections;
using VapeCache.Abstractions.Diagnostics;
using VapeCache.Abstractions.Modules;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Caching.Codecs;
using VapeCache.Infrastructure.Collections;
using VapeCache.Infrastructure.Connections;
using VapeCache.Infrastructure.Modules;

namespace VapeCache.Infrastructure.DependencyInjection;

/// <summary>
/// Represents the vape cache caching module.
/// </summary>
public sealed class VapeCacheCachingModule : Module
{
    /// <summary>
    /// Executes load.
    /// </summary>
    protected override void Load(ContainerBuilder builder)
    {
        CacheTelemetry.EnsureInitialized();

        builder.RegisterInstance(TimeProvider.System).As<TimeProvider>().SingleInstance();

        builder.RegisterType<CurrentCacheService>()
            .As<ICurrentCacheService>()
            .SingleInstance();
        builder.RegisterType<CacheBackendState>()
            .As<ICacheBackendState>()
            .SingleInstance()
            .OnActivated(e => CacheTelemetry.Initialize(e.Instance));
        builder.RegisterType<CacheStatsRegistry>().AsSelf().SingleInstance();
        builder.RegisterType<CurrentCacheStats>().As<ICacheStats>().SingleInstance();
        builder.RegisterType<CacheIntentRegistry>().As<ICacheIntentRegistry>().SingleInstance();

        RegisterStaticOptions(builder, new MemoryCacheOptions());
        RegisterStaticOptions(builder, new InMemorySpillOptions());
        RegisterStaticOptions(builder, new HybridFailoverOptions());
        RegisterStaticOptions(builder, new CacheStampedeOptions());
        RegisterStaticOptions(builder, new RedisCircuitBreakerOptions());
        RegisterStaticOptions(builder, new RedisMultiplexerOptions());
        builder.RegisterType<DefaultEnterpriseFeatureGate>()
            .As<IEnterpriseFeatureGate>()
            .SingleInstance()
            .IfNotRegistered(typeof(IEnterpriseFeatureGate));
        builder.RegisterType<RedisMultiplexerOptionsValidator>().AsSelf().SingleInstance();
        builder.RegisterType<RedisMultiplexerOptionsStartupValidator>().As<IStartable>().SingleInstance();
        builder.RegisterType<MemoryCache>().As<IMemoryCache>().SingleInstance();

        builder.RegisterType<RedisCommandExecutor>()
            .AsSelf()
            .As<IRedisMultiplexerDiagnostics>()
            .SingleInstance();
        builder.RegisterType<InMemoryCommandExecutor>()
            .AsSelf()
            .As<IRedisFallbackCommandExecutor>()
            .SingleInstance();
        // Free tier: No-op spill store (no disk persistence)
        // For Enterprise spill-to-disk, install VapeCache.Persistence package
        builder.RegisterType<NoopSpillStore>()
            .As<IInMemorySpillStore>()
            .As<ISpillStoreDiagnostics>()
            .SingleInstance()
            .OnActivated(e => CacheTelemetry.InitializeSpillDiagnostics(e.Instance))
            .IfNotRegistered(typeof(IInMemorySpillStore));

        builder.RegisterType<RedisCacheService>()
            .UsingConstructor(
                typeof(RedisCommandExecutor),
                typeof(ICurrentCacheService),
                typeof(CacheStatsRegistry),
                typeof(ICacheIntentRegistry))
            .AsSelf()
            .SingleInstance();
        builder.RegisterType<InMemoryCacheService>().AsSelf().As<ICacheFallbackService>().SingleInstance();
        builder.RegisterType<HybridCacheService>()
            .AsSelf()
            .As<IRedisCircuitBreakerState>()
            .As<IRedisFailoverController>()
            .SingleInstance();

        builder.RegisterType<HybridCommandExecutor>()
            .AsSelf()
            .As<IRedisCommandExecutor>()
            .SingleInstance();
        builder.RegisterType<StampedeProtectedCacheService>()
            .UsingConstructor(
                typeof(HybridCacheService),
                typeof(IOptionsMonitor<CacheStampedeOptions>),
                typeof(CacheStatsRegistry))
            .AsSelf()
            .As<ICacheService>()
            .As<ICacheTagService>()
            .SingleInstance();

        builder.RegisterType<SystemTextJsonCodecProvider>()
            .As<ICacheCodecProvider>()
            .SingleInstance();
        builder.RegisterType<VapeCacheClient>().As<IVapeCache>().SingleInstance();
        builder.RegisterType<JsonCacheService>().As<IJsonCache>().SingleInstance();
        builder.RegisterType<ChunkedCacheStreamService>().As<ICacheChunkStreamService>().SingleInstance();
        builder.RegisterType<CacheCollectionFactory>().As<ICacheCollectionFactory>().SingleInstance();
        builder.RegisterType<RedisModuleDetector>()
            .UsingConstructor(typeof(RedisCommandExecutor))
            .As<IRedisModuleDetector>()
            .SingleInstance();
        builder.RegisterType<RedisSearchService>().As<IRedisSearchService>().SingleInstance();
        builder.RegisterType<RedisBloomService>().As<IRedisBloomService>().SingleInstance();
        builder.RegisterType<RedisTimeSeriesService>().As<IRedisTimeSeriesService>().SingleInstance();
    }

    private static void RegisterStaticOptions<T>(ContainerBuilder builder, T value)
        where T : class
    {
        builder.RegisterInstance(new StaticOptionsMonitor<T>(value))
            .As<IOptions<T>>()
            .As<IOptionsMonitor<T>>()
            .SingleInstance()
            .PreserveExistingDefaults();
    }
}
