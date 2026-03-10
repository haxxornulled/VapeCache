using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Extensions.Aspire;

/// <summary>
/// Extension methods for adding VapeCache to .NET Aspire applications.
/// </summary>
public static class AspireVapeCacheExtensions
{
    /// <summary>
    /// Adds VapeCache services configured for high-performance Aspire client usage.
    /// Registers core caching services unless explicitly disabled and returns a fluent client builder.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <param name="registerCoreServices">
    /// When <see langword="true"/>, registers VapeCache core services into <see cref="IServiceCollection"/>.
    /// Set to <see langword="false"/> when container modules (for example Autofac modules) are used instead.
    /// </param>
    /// <returns>A builder for configuring VapeCache Aspire client behavior.</returns>
    public static AspireVapeCacheBuilder AddVapeCacheClientBuilder(
        this IHostApplicationBuilder builder,
        bool registerCoreServices = true)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (registerCoreServices)
        {
            builder.Services.AddVapecacheRedisConnections();
            builder.Services.AddVapecacheCaching();
        }

        builder.Services.TryAddSingleton<IVapeCacheStartupReadiness, VapeCacheStartupReadinessState>();

        return new AspireVapeCacheBuilder(builder);
    }

    /// <summary>
    /// Adds VapeCache services configured for .NET Aspire.
    /// Registers core caching services and returns a builder for fluent configuration.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <returns>A builder for configuring VapeCache with Aspire-specific features.</returns>
    /// <example>
    /// <code>
    /// builder.AddVapeCache()
    ///     .WithRedisFromAspire("redis")
    ///     .WithHealthChecks()
    ///     .WithAspireTelemetry();
    /// </code>
    /// </example>
    public static AspireVapeCacheBuilder AddVapeCache(this IHostApplicationBuilder builder)
        => builder.AddVapeCacheClientBuilder(registerCoreServices: true);

    /// <summary>
    /// Adds VapeCache services and applies the full Aspire kitchen-sink profile in one call.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <param name="configure">Optional kitchen-sink configuration overrides.</param>
    /// <returns>A builder for additional chaining.</returns>
    public static AspireVapeCacheBuilder AddVapeCacheKitchenSink(
        this IHostApplicationBuilder builder,
        Action<VapeCacheKitchenSinkOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddVapeCache().WithKitchenSink(configure);
    }

    /// <summary>
    /// Adds VapeCache core services and applies production observability defaults.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <param name="configure">Optional observability configuration overrides.</param>
    /// <returns>A builder for additional chaining.</returns>
    public static AspireVapeCacheBuilder AddVapeCacheWithProductionObservability(
        this IHostApplicationBuilder builder,
        Action<VapeCacheProductionObservabilityOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddVapeCache().WithProductionObservability(configure);
    }
}
