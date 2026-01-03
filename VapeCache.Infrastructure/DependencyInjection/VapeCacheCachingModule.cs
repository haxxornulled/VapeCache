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

        builder.RegisterInstance(Options.Create(new MemoryCacheOptions())).As<IOptions<MemoryCacheOptions>>().SingleInstance();
        builder.RegisterInstance(Options.Create(new InMemorySpillOptions())).As<IOptions<InMemorySpillOptions>>().SingleInstance();
        builder.RegisterType<MemoryCache>().As<IMemoryCache>().SingleInstance();

        builder.RegisterType<RedisCommandExecutor>().AsSelf().SingleInstance();
        builder.RegisterType<InMemoryCommandExecutor>()
            .AsSelf()
            .As<IRedisFallbackCommandExecutor>()
            .SingleInstance();
        builder.RegisterType<NoopSpillEncryptionProvider>().As<ISpillEncryptionProvider>().SingleInstance();
        builder.RegisterType<FileSpillStore>().As<IInMemorySpillStore>().SingleInstance();

        builder.RegisterType<RedisCacheService>().AsSelf().SingleInstance();
        builder.RegisterType<InMemoryCacheService>().AsSelf().As<ICacheFallbackService>().SingleInstance();
        builder.RegisterType<HybridCacheService>()
            .AsSelf()
            .As<IRedisCircuitBreakerState>()
            .As<IRedisFailoverController>()
            .SingleInstance();

        builder.RegisterType<HybridCommandExecutor>().As<IRedisCommandExecutor>().SingleInstance();
        builder.RegisterType<HybridStampedeCacheService>().As<ICacheService>().SingleInstance();

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
}
