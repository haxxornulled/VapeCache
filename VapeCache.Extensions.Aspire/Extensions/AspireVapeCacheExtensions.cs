using Microsoft.Extensions.DependencyInjection;
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
    {
        // Register core VapeCache services (from VapeCache.Infrastructure)
        builder.Services.AddVapecacheRedisConnections();
        builder.Services.AddVapecacheCaching();

        // Return builder for fluent configuration
        return new AspireVapeCacheBuilder(builder);
    }
}
