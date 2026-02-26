using Microsoft.Extensions.DependencyInjection;
using VapeCache.Abstractions.Caching;
using VapeCache.Infrastructure.Caching;

namespace VapeCache.Extensions.Aspire;

/// <summary>
/// Fluent stampede-protection configuration for Aspire hosts.
/// </summary>
public static class AspireStampedeExtensions
{
    /// <summary>
    /// Applies a named cache stampede profile to <see cref="CacheStampedeOptions"/>.
    /// </summary>
    public static AspireVapeCacheBuilder WithCacheStampedeProfile(
        this AspireVapeCacheBuilder builder,
        CacheStampedeProfile profile,
        Action<CacheStampedeOptionsBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var optionsBuilder = builder.Builder.Services
            .AddOptions<CacheStampedeOptions>()
            .UseCacheStampedeProfile(profile);

        if (configure is not null)
            optionsBuilder.ConfigureCacheStampede(configure);

        return builder;
    }
}
