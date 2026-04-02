using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace VapeCache.Features.Search;

/// <summary>
/// Dependency injection extensions for VapeCache search features.
/// </summary>
public static class SearchServiceCollectionExtensions
{
    /// <summary>
    /// Adds search projection services on top of an existing VapeCache runtime registration.
    /// </summary>
    public static IServiceCollection AddVapeCacheSearch(
        this IServiceCollection services,
        IConfiguration? configuration = null,
        Action<VapeCacheSearchOptions>? configure = null,
        string configurationSectionName = VapeCacheSearchOptions.ConfigurationSectionName)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(configurationSectionName))
            throw new ArgumentException("Configuration section name is required.", nameof(configurationSectionName));

        var optionsBuilder = services.AddOptions<VapeCacheSearchOptions>();
        if (configuration is not null)
            optionsBuilder.Bind(configuration.GetSection(configurationSectionName));

        if (configure is not null)
            services.Configure(configure);

        services.TryAddSingleton(typeof(IRedisHashSearchDocumentStore<>), typeof(RedisHashSearchDocumentStore<>));
        return services;
    }
}
