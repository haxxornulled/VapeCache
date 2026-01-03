using Autofac;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResultDemo.Examples;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Caching;

namespace ResultDemo;

public static class CompositionRoot
{
    public static IContainer BuildContainer(ILoggerFactory loggerFactory)
    {
        var builder = new ContainerBuilder();
        builder.RegisterInstance(loggerFactory).As<ILoggerFactory>().SingleInstance();
        builder.RegisterGeneric(typeof(Logger<>)).As(typeof(ILogger<>)).SingleInstance();

        RegisterVapeCache(builder);

        builder.RegisterType<InMemoryUserStore>().SingleInstance();
        builder.RegisterType<InMemoryUserService>().As<IUserService>().SingleInstance();
        builder.RegisterType<UsersController>();
        builder.RegisterType<UserProvisioningHandler>();
        builder.RegisterType<InMemoryProfileRepository>().As<IUserRepository>().AsSelf().SingleInstance();
        builder.RegisterType<UserProfileFacade>();
        builder.RegisterType<InMemoryUserRepository2>().As<IUserRepository2>().SingleInstance();
        builder.RegisterType<UserLookupService>();
        builder.RegisterType<InMemoryUserQueryService>().As<IUserQueryService>().SingleInstance();
        builder.RegisterType<HybridCacheServiceExamples>();
        return builder.Build();
    }

    private static void RegisterVapeCache(ContainerBuilder builder)
    {
        builder.RegisterInstance(Options.Create(new RedisCircuitBreakerOptions())).As<IOptions<RedisCircuitBreakerOptions>>();
        builder.RegisterInstance(Options.Create(new CacheStampedeOptions())).As<IOptions<CacheStampedeOptions>>();
        builder.RegisterInstance(Options.Create(new RedisReconciliationOptions())).As<IOptions<RedisReconciliationOptions>>();

        builder.RegisterInstance(TimeProvider.System).As<TimeProvider>().SingleInstance();
        builder.RegisterType<CurrentCacheService>().As<ICurrentCacheService>().SingleInstance();
        builder.RegisterType<CacheStatsRegistry>().AsSelf().SingleInstance();
        var memoryOptions = new MemoryCacheOptions();
        builder.RegisterInstance(Options.Create(memoryOptions)).As<IOptions<MemoryCacheOptions>>();
        builder.RegisterType<MemoryCache>().As<IMemoryCache>().SingleInstance();
        builder.RegisterType<StubRedisCommandExecutor>().As<IRedisCommandExecutor>().SingleInstance();
        builder.RegisterType<NoopReconciliationService>().As<IRedisReconciliationService>().SingleInstance();

        builder.RegisterType<RedisCacheService>().AsSelf().SingleInstance();
        builder.RegisterType<InMemoryCacheService>().AsSelf().SingleInstance();
        builder.RegisterType<HybridCacheService>()
            .As<ICacheService>()
            .As<IRedisCircuitBreakerState>()
            .As<IRedisFailoverController>()
            .SingleInstance();
    }
}
