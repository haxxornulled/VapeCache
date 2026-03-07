using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace VapeCache.Features.Invalidation;

/// <summary>
/// Dependency injection extensions for invalidation features.
/// </summary>
public static class InvalidationServiceCollectionExtensions
{
    public static IServiceCollection AddVapeCacheInvalidation(
        this IServiceCollection services,
        IConfiguration? configuration = null,
        Action<CacheInvalidationOptions>? configure = null,
        string configurationSectionName = CacheInvalidationOptions.ConfigurationSectionName)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(configurationSectionName))
            throw new ArgumentException("Configuration section name is required.", nameof(configurationSectionName));

        var optionsBuilder = services.AddOptions<CacheInvalidationOptions>();
        if (configuration is not null)
            optionsBuilder.Bind(configuration.GetSection(configurationSectionName));

        if (configure is not null)
            services.Configure(configure);

        services.TryAddSingleton<ICacheInvalidationExecutor, CacheInvalidationExecutor>();
        services.TryAddSingleton<ICacheInvalidationDispatcher, CacheInvalidationDispatcher>();

        return services;
    }

    public static IServiceCollection AddCacheInvalidationPolicy<TEvent, TPolicy>(this IServiceCollection services)
        where TPolicy : class, ICacheInvalidationPolicy<TEvent>
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ICacheInvalidationPolicy<TEvent>, TPolicy>());

        return services;
    }

    public static IServiceCollection AddCacheInvalidationPolicy<TEvent>(
        this IServiceCollection services,
        Func<IServiceProvider, ICacheInvalidationPolicy<TEvent>> factory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(factory);

        services.AddSingleton(factory);

        return services;
    }

    public static IServiceCollection AddTagInvalidationPolicy<TEvent>(
        this IServiceCollection services,
        Func<TEvent, IEnumerable<string>?> tagsSelector,
        Func<TEvent, bool>? predicate = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(tagsSelector);

        return services.AddCacheInvalidationPolicy<TEvent>(sp =>
            new TagInvalidationPolicy<TEvent>(tagsSelector, predicate));
    }

    public static IServiceCollection AddZoneInvalidationPolicy<TEvent>(
        this IServiceCollection services,
        Func<TEvent, IEnumerable<string>?> zonesSelector,
        Func<TEvent, bool>? predicate = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(zonesSelector);

        return services.AddCacheInvalidationPolicy<TEvent>(sp =>
            new ZoneInvalidationPolicy<TEvent>(zonesSelector, predicate));
    }

    public static IServiceCollection AddKeyInvalidationPolicy<TEvent>(
        this IServiceCollection services,
        Func<TEvent, IEnumerable<string>?> keysSelector,
        Func<TEvent, bool>? predicate = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(keysSelector);

        return services.AddCacheInvalidationPolicy<TEvent>(sp =>
            new KeyInvalidationPolicy<TEvent>(keysSelector, predicate));
    }

    public static IServiceCollection AddEntityInvalidationPolicy<TEvent>(
        this IServiceCollection services,
        string entityName,
        Func<TEvent, IEnumerable<string>?> idsSelector,
        Func<TEvent, IEnumerable<string>?>? zonesSelector = null,
        IEnumerable<string>? keyPrefixes = null,
        Func<TEvent, bool>? predicate = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        ArgumentNullException.ThrowIfNull(idsSelector);

        return services.AddCacheInvalidationPolicy<TEvent>(sp =>
            new EntityInvalidationPolicy<TEvent>(
                entityName,
                idsSelector,
                zonesSelector,
                keyPrefixes,
                predicate));
    }

    /// <summary>
    /// Registers a balanced entity invalidation policy for small website workloads.
    /// </summary>
    public static IServiceCollection AddSmallWebsiteEntityInvalidationPolicy<TEvent>(
        this IServiceCollection services,
        string entityName,
        Func<TEvent, IEnumerable<string>?> idsSelector,
        Func<TEvent, IEnumerable<string>?>? zonesSelector = null,
        Func<TEvent, bool>? predicate = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        ArgumentNullException.ThrowIfNull(idsSelector);

        return services.AddEntityInvalidationPolicy(
            entityName: entityName,
            idsSelector: idsSelector,
            zonesSelector: zonesSelector,
            keyPrefixes: [],
            predicate: predicate);
    }

    /// <summary>
    /// Registers an entity invalidation policy for high-throughput web workloads.
    /// </summary>
    public static IServiceCollection AddHighTrafficEntityInvalidationPolicy<TEvent>(
        this IServiceCollection services,
        string entityName,
        Func<TEvent, IEnumerable<string>?> idsSelector,
        Func<TEvent, IEnumerable<string>?>? zonesSelector = null,
        IEnumerable<string>? keyPrefixes = null,
        Func<TEvent, bool>? predicate = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        ArgumentNullException.ThrowIfNull(idsSelector);

        return services.AddEntityInvalidationPolicy(
            entityName: entityName,
            idsSelector: idsSelector,
            zonesSelector: zonesSelector,
            keyPrefixes: keyPrefixes ?? [entityName],
            predicate: predicate);
    }

    /// <summary>
    /// Registers a lightweight key-only invalidation policy for desktop and local app workloads.
    /// </summary>
    public static IServiceCollection AddDesktopKeyInvalidationPolicy<TEvent>(
        this IServiceCollection services,
        Func<TEvent, IEnumerable<string>?> keysSelector,
        Func<TEvent, bool>? predicate = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(keysSelector);

        return services.AddKeyInvalidationPolicy(
            keysSelector: keysSelector,
            predicate: predicate);
    }
}
