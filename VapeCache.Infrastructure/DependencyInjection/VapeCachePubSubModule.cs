using Autofac;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Infrastructure.DependencyInjection;

/// <summary>
/// Registers Redis pub/sub services for Autofac hosts.
/// </summary>
public sealed class VapeCachePubSubModule : Module
{
    /// <summary>
    /// Executes module registrations.
    /// </summary>
    protected override void Load(ContainerBuilder builder)
    {
        RegisterStaticOptions(builder, new RedisPubSubOptions());
        builder.RegisterType<RedisPubSubOptionsValidator>().AsSelf().SingleInstance();
        builder.RegisterType<RedisPubSubOptionsStartupValidator>().As<IStartable>().SingleInstance();
        builder.RegisterType<RedisPubSubService>()
            .As<IRedisPubSubService>()
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
