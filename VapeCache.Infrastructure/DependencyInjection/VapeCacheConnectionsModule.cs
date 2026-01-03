using Autofac;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Infrastructure.DependencyInjection;

public sealed class VapeCacheConnectionsModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<RedisConnectionFactory>().AsSelf().SingleInstance();
        builder.RegisterType<CircuitBreakerRedisConnectionFactory>().As<IRedisConnectionFactory>().SingleInstance();

        builder.RegisterType<RedisConnectionPool>()
            .AsSelf()
            .As<IRedisConnectionPool>()
            .As<IRedisConnectionPoolReaper>()
            .SingleInstance();
    }
}
