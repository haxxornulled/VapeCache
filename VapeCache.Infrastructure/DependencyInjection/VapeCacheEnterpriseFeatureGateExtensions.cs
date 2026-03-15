using Autofac;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Infrastructure.DependencyInjection;

/// <summary>
/// Autofac helpers for replacing the default enterprise feature gate wiring.
/// </summary>
public static class VapeCacheEnterpriseFeatureGateExtensions
{
    /// <summary>
    /// Registers a custom enterprise feature gate implementation.
    /// </summary>
    public static ContainerBuilder RegisterVapeCacheEnterpriseFeatureGate<TFeatureGate>(this ContainerBuilder builder)
        where TFeatureGate : class, IEnterpriseFeatureGate
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.RegisterType<TFeatureGate>()
            .As<IEnterpriseFeatureGate>()
            .SingleInstance();
        return builder;
    }

    /// <summary>
    /// Registers a custom enterprise feature gate factory.
    /// </summary>
    public static ContainerBuilder RegisterVapeCacheEnterpriseFeatureGate(
        this ContainerBuilder builder,
        Func<IComponentContext, IEnterpriseFeatureGate> factory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(factory);

        builder.Register(factory)
            .As<IEnterpriseFeatureGate>()
            .SingleInstance();
        return builder;
    }
}
