using Autofac;
using VapeCache.Infrastructure.DependencyInjection;

namespace VapeCache.Extensions.PubSub;

/// <summary>
/// Autofac extensions for enabling Redis pub/sub services.
/// </summary>
public static class VapeCachePubSubAutofacExtensions
{
    /// <summary>
    /// Registers the VapeCache Redis pub/sub Autofac module.
    /// </summary>
    public static ContainerBuilder AddVapeCachePubSub(this ContainerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.RegisterModule<VapeCachePubSubModule>();
        return builder;
    }
}
