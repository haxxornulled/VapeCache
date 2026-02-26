using Microsoft.AspNetCore.OutputCaching;
using VapeCache.Extensions.AspNetCore;

namespace VapeCache.Extensions.Aspire;

/// <summary>
/// Fluent Aspire hooks for ASP.NET Core output caching with VapeCache-backed storage.
/// </summary>
public static class AspireOutputCachingExtensions
{
    /// <summary>
    /// Adds ASP.NET Core output caching and replaces the default output-cache store with VapeCache.
    /// </summary>
    public static AspireVapeCacheBuilder WithAspNetCoreOutputCaching(
        this AspireVapeCacheBuilder builder,
        Action<OutputCacheOptions>? configureOutputCache = null,
        Action<VapeCacheOutputCacheStoreOptions>? configureStore = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Builder.Services.AddVapeCacheOutputCaching(configureOutputCache, configureStore);
        return builder;
    }

    /// <summary>
    /// Adds sticky-session affinity hint options for clustered hosts that use local in-memory failover.
    /// </summary>
    public static AspireVapeCacheBuilder WithFailoverAffinityHints(
        this AspireVapeCacheBuilder builder,
        Action<VapeCacheFailoverAffinityOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Builder.Services.AddVapeCacheFailoverAffinityHints(configure);
        return builder;
    }
}
