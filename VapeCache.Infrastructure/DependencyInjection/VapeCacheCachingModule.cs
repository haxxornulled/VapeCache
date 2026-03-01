using System.Text.Json;
using Autofac;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Collections;
using VapeCache.Abstractions.Connections;
using VapeCache.Abstractions.Modules;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Caching.Codecs;
using VapeCache.Infrastructure.Collections;
using VapeCache.Infrastructure.Connections;
using VapeCache.Infrastructure.Modules;

namespace VapeCache.Infrastructure.DependencyInjection;

public sealed class VapeCacheCachingModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterInstance(TimeProvider.System).As<TimeProvider>().SingleInstance();

        builder.RegisterType<CurrentCacheService>()
            .As<ICurrentCacheService>()
            .SingleInstance()
            .OnActivated(e => CacheTelemetry.Initialize(e.Instance));
        builder.RegisterType<CacheStatsRegistry>().AsSelf().SingleInstance();
        builder.RegisterType<CurrentCacheStats>().As<ICacheStats>().SingleInstance();
        builder.RegisterType<CacheIntentRegistry>().As<ICacheIntentRegistry>().SingleInstance();

        RegisterStaticOptions(builder, new MemoryCacheOptions());
        RegisterStaticOptions(builder, new InMemorySpillOptions());
        RegisterStaticOptions(builder, new CacheStampedeOptions());
        RegisterStaticOptions(builder, new RedisCircuitBreakerOptions());
        RegisterStaticOptions(builder, new RedisMultiplexerOptions());
        builder.RegisterType<MemoryCache>().As<IMemoryCache>().SingleInstance();

        builder.RegisterType<RedisCommandExecutor>().AsSelf().SingleInstance();
        builder.Register(ctx => (IRedisMultiplexerDiagnostics)ctx.Resolve<RedisCommandExecutor>())
            .As<IRedisMultiplexerDiagnostics>()
            .SingleInstance();
        builder.RegisterType<InMemoryCommandExecutor>()
            .AsSelf()
            .As<IRedisFallbackCommandExecutor>()
            .SingleInstance();
        // Free tier: No-op spill store (no disk persistence)
        // For Enterprise spill-to-disk, install VapeCache.Persistence package
        builder.RegisterType<NoopSpillStore>().As<IInMemorySpillStore>().SingleInstance();

        builder.Register(ctx => new RedisCacheService(
                ctx.Resolve<RedisCommandExecutor>(),
                ctx.Resolve<ICurrentCacheService>(),
                ctx.Resolve<CacheStatsRegistry>(),
                ctx.ResolveOptional<ICacheIntentRegistry>()))
            .AsSelf()
            .SingleInstance();
        builder.RegisterType<InMemoryCacheService>().AsSelf().As<ICacheFallbackService>().SingleInstance();
        builder.RegisterType<HybridCacheService>()
            .AsSelf()
            .SingleInstance();
        builder.Register(ctx => (IRedisCircuitBreakerState)ctx.Resolve<HybridCacheService>())
            .As<IRedisCircuitBreakerState>()
            .SingleInstance();
        builder.Register(ctx => (IRedisFailoverController)ctx.Resolve<HybridCacheService>())
            .As<IRedisFailoverController>()
            .SingleInstance();

        builder.RegisterType<HybridCommandExecutor>()
            .AsSelf()
            .SingleInstance();
        builder.Register(ctx => (IRedisCommandExecutor)ctx.Resolve<HybridCommandExecutor>())
            .As<IRedisCommandExecutor>()
            .SingleInstance();
        builder.Register(ctx => new StampedeProtectedCacheService(
                ctx.Resolve<HybridCacheService>(),
                ctx.Resolve<IOptionsMonitor<CacheStampedeOptions>>(),
                ctx.Resolve<CacheStatsRegistry>().GetOrCreate(CacheStatsNames.Hybrid)))
            .As<ICacheService>()
            .SingleInstance();

        builder.RegisterType<SystemTextJsonCodecProvider>()
            .As<ICacheCodecProvider>()
            .SingleInstance()
            .WithParameter("options", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        builder.RegisterType<VapeCacheClient>().As<IVapeCache>().SingleInstance();
        builder.RegisterType<JsonCacheService>().As<IJsonCache>().SingleInstance();
        builder.RegisterType<CacheCollectionFactory>().As<ICacheCollectionFactory>().SingleInstance();
        builder.RegisterType<RedisModuleDetector>().As<IRedisModuleDetector>().SingleInstance();
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
