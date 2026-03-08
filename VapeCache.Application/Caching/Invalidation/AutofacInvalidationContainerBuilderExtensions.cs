using Autofac;
using VapeCache.Application.Abstractions;
using VapeCache.Application.Caching.Invalidation.Commands;
using VapeCache.Application.Caching.Invalidation.Events;
using VapeCache.Application.Caching.Invalidation.Handlers;
using VapeCache.Application.Caching.Invalidation.Policies;
using VapeCache.Features.Invalidation;

namespace VapeCache.Application.Caching.Invalidation;

/// <summary>
/// Autofac registrations for application-level invalidation defaults.
/// </summary>
public static class AutofacInvalidationContainerBuilderExtensions
{
    /// <summary>
    /// Executes add vape cache application invalidation.
    /// </summary>
    public static ContainerBuilder AddVapeCacheApplicationInvalidation(
        this ContainerBuilder builder,
        Action<CacheInvalidationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddVapeCacheInvalidation(configure);
        builder.AddVapeCacheApplicationInvalidationDefaults();
        return builder;
    }

    /// <summary>
    /// Executes add vape cache application invalidation defaults.
    /// </summary>
    public static ContainerBuilder AddVapeCacheApplicationInvalidationDefaults(this ContainerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.RegisterType<CacheInvalidationEventPublisher>()
            .As<ICacheInvalidationEventPublisher>()
            .SingleInstance();

        builder.AddCacheInvalidationPolicy<EntityCacheChangedEvent, ProfileAwareEntityCacheChangedInvalidationPolicy>();
        builder.AddCacheInvalidationPolicy<CacheTagsInvalidatedEvent, CacheTagsInvalidatedEventPolicy>();
        builder.AddCacheInvalidationPolicy<CacheZonesInvalidatedEvent, CacheZonesInvalidatedEventPolicy>();
        builder.AddCacheInvalidationPolicy<CacheKeysInvalidatedEvent, CacheKeysInvalidatedEventPolicy>();

        builder.RegisterType<InvalidateEntityCacheCommandHandler>()
            .As<ICommandHandler<InvalidateEntityCacheCommand, CacheInvalidationExecutionResult>>()
            .InstancePerDependency();
        builder.RegisterType<InvalidateCacheTagsCommandHandler>()
            .As<ICommandHandler<InvalidateCacheTagsCommand, CacheInvalidationExecutionResult>>()
            .InstancePerDependency();
        builder.RegisterType<InvalidateCacheZonesCommandHandler>()
            .As<ICommandHandler<InvalidateCacheZonesCommand, CacheInvalidationExecutionResult>>()
            .InstancePerDependency();
        builder.RegisterType<InvalidateCacheKeysCommandHandler>()
            .As<ICommandHandler<InvalidateCacheKeysCommand, CacheInvalidationExecutionResult>>()
            .InstancePerDependency();

        return builder;
    }
}
