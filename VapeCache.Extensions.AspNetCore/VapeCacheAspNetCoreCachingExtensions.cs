using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace VapeCache.Extensions.AspNetCore;

/// <summary>
/// ASP.NET Core pipeline hooks for using VapeCache as the output-cache store.
/// </summary>
public static class VapeCacheAspNetCoreCachingExtensions
{
    /// <summary>
    /// Adds ASP.NET Core output caching with a <see cref="VapeCacheOutputCacheStore"/> and binds store options from configuration.
    /// </summary>
    public static IServiceCollection AddVapeCacheOutputCaching(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<OutputCacheOptions>? configureOutputCache = null,
        Action<VapeCacheOutputCacheStoreOptions>? configureStore = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddVapeCacheOutputCaching(configureOutputCache, configureStore);
        services.Configure<VapeCacheOutputCacheStoreOptions>(configuration.GetSection("VapeCacheOutputCache"));
        return services;
    }

    /// <summary>
    /// Adds ASP.NET Core output caching with a <see cref="VapeCacheOutputCacheStore"/>.
    /// </summary>
    public static IServiceCollection AddVapeCacheOutputCaching(
        this IServiceCollection services,
        Action<OutputCacheOptions>? configureOutputCache = null,
        Action<VapeCacheOutputCacheStoreOptions>? configureStore = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<VapeCacheOutputCacheStoreOptions>()
            .Configure(options =>
            {
                options.KeyPrefix = string.IsNullOrWhiteSpace(options.KeyPrefix)
                    ? "vapecache:output"
                    : options.KeyPrefix.Trim();
                if (options.DefaultTtl <= TimeSpan.Zero)
                    options.DefaultTtl = TimeSpan.FromSeconds(30);
            })
            .Validate(static o => !string.IsNullOrWhiteSpace(o.KeyPrefix), "KeyPrefix is required.")
            .Validate(static o => o.DefaultTtl > TimeSpan.Zero, "DefaultTtl must be greater than zero.")
            .ValidateOnStart();

        if (configureStore is not null)
            services.Configure(configureStore);

        services.AddOutputCache(options => configureOutputCache?.Invoke(options));
        services.Replace(ServiceDescriptor.Singleton<IOutputCacheStore, VapeCacheOutputCacheStore>());

        return services;
    }

    /// <summary>
    /// Adds sticky-session affinity hint options for local in-memory failover in clustered deployments.
    /// </summary>
    public static IServiceCollection AddVapeCacheFailoverAffinityHints(
        this IServiceCollection services,
        Action<VapeCacheFailoverAffinityOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<VapeCacheFailoverAffinityOptions>()
            .Validate(static o => !string.IsNullOrWhiteSpace(o.NodeId), "NodeId is required.")
            .Validate(static o => !string.IsNullOrWhiteSpace(o.NodeHeaderName), "NodeHeaderName is required.")
            .Validate(static o => !string.IsNullOrWhiteSpace(o.StateHeaderName), "StateHeaderName is required.")
            .Validate(static o => !string.IsNullOrWhiteSpace(o.CookieName), "CookieName is required.")
            .Validate(static o => o.CookieTtl > TimeSpan.Zero, "CookieTtl must be greater than zero.")
            .ValidateOnStart();

        if (configure is not null)
            services.Configure(configure);

        return services;
    }

    /// <summary>
    /// Adds the ASP.NET Core output-cache middleware.
    /// </summary>
    public static IApplicationBuilder UseVapeCacheOutputCaching(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseOutputCache();
    }

    /// <summary>
    /// Emits node-affinity hints (headers/cookie) so upstream load balancers can keep sessions sticky during failover.
    /// </summary>
    public static IApplicationBuilder UseVapeCacheFailoverAffinityHints(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<VapeCacheFailoverAffinityMiddleware>();
    }

    /// <summary>
    /// Applies output-cache metadata to a minimal API endpoint using VapeCache-backed store.
    /// </summary>
    public static RouteHandlerBuilder CacheWithVapeCache(
        this RouteHandlerBuilder builder,
        string? policyName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return string.IsNullOrWhiteSpace(policyName)
            ? builder.CacheOutput()
            : builder.CacheOutput(policyName);
    }
}
