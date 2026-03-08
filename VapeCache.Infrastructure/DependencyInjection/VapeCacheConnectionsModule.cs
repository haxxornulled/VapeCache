using Autofac;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Infrastructure.DependencyInjection;

public sealed class VapeCacheConnectionsModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        RedisTelemetry.EnsureInitialized();

        RegisterStaticOptions(builder, new RedisConnectionOptions());
        RegisterStaticOptions(builder, new RedisCircuitBreakerOptions());
        builder.RegisterType<RedisConnectionOptionsValidator>().AsSelf().SingleInstance();
        builder.RegisterType<RedisConnectionOptionsStartupValidator>().As<IStartable>().SingleInstance();

        builder.RegisterType<RedisConnectionFactory>().AsSelf().SingleInstance();
        builder.RegisterType<CircuitBreakerRedisConnectionFactory>().As<IRedisConnectionFactory>().SingleInstance();

        builder.RegisterType<RedisConnectionPool>()
            .AsSelf()
            .As<IRedisConnectionPool>()
            .As<IRedisConnectionPoolReaper>()
            .SingleInstance();
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
