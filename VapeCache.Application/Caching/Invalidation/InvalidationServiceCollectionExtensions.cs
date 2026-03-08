using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VapeCache.Application.Abstractions;
using VapeCache.Application.Caching.Invalidation.Commands;
using VapeCache.Application.Caching.Invalidation.Events;
using VapeCache.Application.Caching.Invalidation.Handlers;
using VapeCache.Application.Caching.Invalidation.Policies;
using VapeCache.Features.Invalidation;

namespace VapeCache.Application.Caching.Invalidation;

/// <summary>
/// Application-level invalidation defaults that map common domain events to policy execution.
/// </summary>
public static class InvalidationServiceCollectionExtensions
{
    /// <summary>
    /// Adds invalidation runtime services and the default application event-policy mappings.
    /// </summary>
    public static IServiceCollection AddVapeCacheApplicationInvalidation(
        this IServiceCollection services,
        Action<CacheInvalidationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddVapeCacheInvalidation(configure: configure);
        services.AddVapeCacheApplicationInvalidationDefaults();
        return services;
    }

    /// <summary>
    /// Adds default application event-policy mappings for cache invalidation.
    /// </summary>
    public static IServiceCollection AddVapeCacheApplicationInvalidationDefaults(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ICacheInvalidationEventPublisher, CacheInvalidationEventPublisher>();
        services.AddCacheInvalidationPolicy<EntityCacheChangedEvent, ProfileAwareEntityCacheChangedInvalidationPolicy>();
        services.AddCacheInvalidationPolicy<CacheTagsInvalidatedEvent, CacheTagsInvalidatedEventPolicy>();
        services.AddCacheInvalidationPolicy<CacheZonesInvalidatedEvent, CacheZonesInvalidatedEventPolicy>();
        services.AddCacheInvalidationPolicy<CacheKeysInvalidatedEvent, CacheKeysInvalidatedEventPolicy>();
        services.TryAddTransient<ICommandHandler<InvalidateEntityCacheCommand, CacheInvalidationExecutionResult>, InvalidateEntityCacheCommandHandler>();
        services.TryAddTransient<ICommandHandler<InvalidateCacheTagsCommand, CacheInvalidationExecutionResult>, InvalidateCacheTagsCommandHandler>();
        services.TryAddTransient<ICommandHandler<InvalidateCacheZonesCommand, CacheInvalidationExecutionResult>, InvalidateCacheZonesCommandHandler>();
        services.TryAddTransient<ICommandHandler<InvalidateCacheKeysCommand, CacheInvalidationExecutionResult>, InvalidateCacheKeysCommandHandler>();
        return services;
    }
}
