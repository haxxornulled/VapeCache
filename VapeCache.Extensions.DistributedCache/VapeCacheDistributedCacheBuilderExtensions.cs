using Microsoft.Extensions.Configuration;
using VapeCache.Extensions.DependencyInjection;

namespace VapeCache.Extensions.DistributedCache;

/// <summary>
/// Fluent builder extensions for enabling the VapeCache distributed-cache adapter.
/// </summary>
public static class VapeCacheDistributedCacheBuilderExtensions
{
    /// <summary>
    /// Enables the <see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/> adapter on an existing VapeCache DI builder.
    /// </summary>
    public static VapeCacheDependencyInjectionBuilder UseDistributedCacheAdapter(
        this VapeCacheDependencyInjectionBuilder builder,
        Action<VapeCacheDistributedCacheOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddVapeCacheDistributedCache(configure);
        return builder;
    }

    /// <summary>
    /// Enables the distributed-cache adapter and binds adapter options from configuration.
    /// </summary>
    public static VapeCacheDependencyInjectionBuilder UseDistributedCacheAdapter(
        this VapeCacheDependencyInjectionBuilder builder,
        IConfiguration configuration,
        string sectionName = "VapeCacheDistributedCache")
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddVapeCacheDistributedCache(configuration, sectionName);
        return builder;
    }
}
