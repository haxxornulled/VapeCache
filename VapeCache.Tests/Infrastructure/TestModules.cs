using Autofac;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Tests.Infrastructure;

/// <summary>
/// Autofac modules for composing real services with test seams.
/// </summary>
internal static class TestModules
{
    public static IContainer BuildCacheContainer(
        IRedisCommandExecutor redisExecutor,
        RedisCircuitBreakerOptions breakerOptions,
        CacheStampedeOptions stampedeOptions,
        RedisReconciliationOptions reconciliationOptions,
        TimeProvider? timeProvider = null,
        ILoggerFactory? loggerFactory = null)
    {
        var builder = new ContainerBuilder();

        // Logging
        loggerFactory ??= NullLoggerFactory.Instance;
        builder.RegisterInstance(loggerFactory).As<ILoggerFactory>().SingleInstance();
        builder.RegisterGeneric(typeof(Logger<>)).As(typeof(ILogger<>)).SingleInstance();

        // Options
        builder.RegisterInstance<IOptionsMonitor<RedisCircuitBreakerOptions>>(new TestOptionsMonitor<RedisCircuitBreakerOptions>(breakerOptions));
        builder.RegisterInstance(Options.Create(breakerOptions)).As<IOptions<RedisCircuitBreakerOptions>>();

        builder.RegisterInstance<IOptionsMonitor<CacheStampedeOptions>>(new TestOptionsMonitor<CacheStampedeOptions>(stampedeOptions));
        builder.RegisterInstance(Options.Create(stampedeOptions)).As<IOptions<CacheStampedeOptions>>();

        builder.RegisterInstance<IOptionsMonitor<RedisReconciliationOptions>>(new TestOptionsMonitor<RedisReconciliationOptions>(reconciliationOptions));
        builder.RegisterInstance(Options.Create(reconciliationOptions)).As<IOptions<RedisReconciliationOptions>>();

        // Core services
        builder.RegisterInstance(timeProvider ?? TimeProvider.System).As<TimeProvider>().SingleInstance();
        builder.RegisterType<CurrentCacheService>().As<ICurrentCacheService>().SingleInstance();
        builder.RegisterType<CacheStatsRegistry>().AsSelf().SingleInstance();
        builder.RegisterType<CurrentCacheStats>().As<ICacheStats>().SingleInstance();
        builder.RegisterInstance(Options.Create(new MemoryCacheOptions())).As<IOptions<MemoryCacheOptions>>();
        builder.RegisterInstance<IOptionsMonitor<MemoryCacheOptions>>(new TestOptionsMonitor<MemoryCacheOptions>(new MemoryCacheOptions()));
        builder.RegisterType<MemoryCache>().As<IMemoryCache>().SingleInstance();

        // Executors
        builder.RegisterInstance(redisExecutor).As<IRedisCommandExecutor>().SingleInstance();
        builder.RegisterType<InMemoryCommandExecutor>().AsSelf().SingleInstance();

        // Cache services
        builder.RegisterType<RedisCacheService>().AsSelf().SingleInstance();
        builder.RegisterType<InMemoryCacheService>().AsSelf().SingleInstance();
        builder.RegisterType<HybridCacheService>()
            .AsSelf()
            .As<IRedisCircuitBreakerState>()
            .As<IRedisFailoverController>()
            .SingleInstance();

        // Optional reconciliation service stub
        builder.RegisterType<NoopReconciliationService>().As<IRedisReconciliationService>().SingleInstance();

        return builder.Build();
    }
}

internal sealed class NoopReconciliationService : IRedisReconciliationService
{
    public int PendingOperations => 0;
    public void TrackWrite(string key, ReadOnlyMemory<byte> value, TimeSpan? expiry) { }
    public void TrackDelete(string key) { }
    public ValueTask ReconcileAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    public void Clear() { }
}
