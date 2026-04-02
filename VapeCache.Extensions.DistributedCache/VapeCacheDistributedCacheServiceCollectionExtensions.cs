using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;

namespace VapeCache.Extensions.DistributedCache;

/// <summary>
/// IServiceCollection extensions for wiring the VapeCache <see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/> adapter.
/// </summary>
public static class VapeCacheDistributedCacheServiceCollectionExtensions
{
    /// <summary>
    /// Registers the VapeCache distributed-cache adapter over an existing VapeCache runtime registration.
    /// </summary>
    public static IServiceCollection AddVapeCacheDistributedCache(
        this IServiceCollection services,
        Action<VapeCacheDistributedCacheOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<VapeCacheDistributedCacheOptions>();
        if (configure is not null)
            services.Configure(configure);

        services.TryAddSingleton<VapeCacheDistributedCache>();
        services.Replace(ServiceDescriptor.Singleton<Microsoft.Extensions.Caching.Distributed.IDistributedCache>(
            static sp => sp.GetRequiredService<VapeCacheDistributedCache>()));
        services.Replace(ServiceDescriptor.Singleton<Microsoft.Extensions.Caching.Distributed.IBufferDistributedCache>(
            static sp => sp.GetRequiredService<VapeCacheDistributedCache>()));

        return services;
    }

    /// <summary>
    /// Registers the VapeCache distributed-cache adapter and binds options from configuration.
    /// </summary>
    public static IServiceCollection AddVapeCacheDistributedCache(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = "VapeCacheDistributedCache")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);

        services.AddOptions<VapeCacheDistributedCacheOptions>()
            .Bind(configuration.GetSection(sectionName));

        return services.AddVapeCacheDistributedCache();
    }
}
