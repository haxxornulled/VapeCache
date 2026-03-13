using Microsoft.Extensions.Configuration;
using VapeCache.Extensions.DependencyInjection;

namespace VapeCache.Extensions.PubSub;

/// <summary>
/// Fluent builder extensions for enabling Redis pub/sub.
/// </summary>
public static class VapeCachePubSubBuilderExtensions
{
    /// <summary>
    /// Enables Redis pub/sub services for an existing VapeCache DI builder.
    /// </summary>
    public static VapeCacheDependencyInjectionBuilder UseRedisPubSub(
        this VapeCacheDependencyInjectionBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddVapeCachePubSub();
        return builder;
    }

    /// <summary>
    /// Enables Redis pub/sub services and binds pub/sub options from configuration.
    /// </summary>
    public static VapeCacheDependencyInjectionBuilder UseRedisPubSub(
        this VapeCacheDependencyInjectionBuilder builder,
        IConfiguration configuration,
        string sectionName = "RedisPubSub")
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddVapeCachePubSub(configuration, sectionName);
        return builder;
    }
}
