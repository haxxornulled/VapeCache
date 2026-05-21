using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Extensions.DependencyInjection;

/// <summary>
/// IServiceCollection extensions for one-call VapeCache runtime wiring.
/// </summary>
public static class VapeCacheServiceCollectionExtensions
{
    /// <summary>
    /// Registers the VapeCache runtime (connections + cache services) and returns a fluent builder.
    /// </summary>
    public static VapeCacheDependencyInjectionBuilder AddVapeCache(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddLogging();
        services.AddVapeCacheRedisConnections();
        services.AddVapeCacheCaching();

        return new VapeCacheDependencyInjectionBuilder(services);
    }

    /// <summary>
    /// Registers the VapeCache runtime and binds runtime option sections from configuration.
    /// </summary>
    public static VapeCacheDependencyInjectionBuilder AddVapeCache(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<VapeCacheConfigurationBindingOptions>? configureBinding = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        return services
            .AddVapeCache()
            .BindFromConfiguration(configuration, configureBinding);
    }

    /// <summary>
    /// Registers the in-memory-only VapeCache runtime and returns a fluent builder.
    /// This mode does not require Redis and is intended for local or lightweight hosts.
    /// </summary>
    public static VapeCacheDependencyInjectionBuilder AddVapeCacheInMemory(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddLogging();
        services.AddVapeCacheInMemoryCaching();

        return new VapeCacheDependencyInjectionBuilder(services);
    }

    /// <summary>
    /// Registers the in-memory-only VapeCache runtime and binds only memory-relevant option sections.
    /// Redis/hybrid sections are intentionally ignored in this mode.
    /// </summary>
    public static VapeCacheDependencyInjectionBuilder AddVapeCacheInMemory(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<VapeCacheConfigurationBindingOptions>? configureBinding = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        return services
            .AddVapeCacheInMemory()
            .BindFromConfiguration(configuration, options =>
            {
                options.BindRedisConnection = false;
                options.BindRedisMultiplexer = false;
                options.BindRedisCircuitBreaker = false;
                options.BindHybridFailover = false;
                configureBinding?.Invoke(options);
                options.BindRedisConnection = false;
                options.BindRedisMultiplexer = false;
                options.BindRedisCircuitBreaker = false;
                options.BindHybridFailover = false;
            });
    }
}
