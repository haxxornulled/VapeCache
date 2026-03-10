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
        services.AddVapecacheRedisConnections();
        services.AddVapecacheCaching();

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
}
