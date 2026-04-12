using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VapeCache.Extensions.DependencyInjection;

namespace VapeCache.Extensions.KeyDB;

/// <summary>
/// Service registration helpers for KeyDB-backed VapeCache deployments.
/// </summary>
public static class VapeCacheKeyDbServiceCollectionExtensions
{
    /// <summary>
    /// Registers the standard VapeCache runtime using the explicit KeyDB package boundary.
    /// </summary>
    public static VapeCacheDependencyInjectionBuilder AddVapeCacheKeyDb(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddVapeCache();
    }

    /// <summary>
    /// Registers the standard VapeCache runtime and binds options from configuration.
    /// Defaults the Redis connection section to <c>KeyDbConnection</c> for explicit backend intent.
    /// </summary>
    public static VapeCacheDependencyInjectionBuilder AddVapeCacheKeyDb(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<VapeCacheConfigurationBindingOptions>? configureBinding = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        return services.AddVapeCache(configuration, options =>
        {
            options.RedisConnectionSectionName = "KeyDbConnection";
            configureBinding?.Invoke(options);
        });
    }
}
