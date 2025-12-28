using Microsoft.Extensions.Hosting;

namespace VapeCache.Extensions.Aspire;

/// <summary>
/// Builder for configuring VapeCache with .NET Aspire integration.
/// Provides fluent API for service discovery, health checks, and telemetry.
/// </summary>
public sealed class AspireVapeCacheBuilder
{
    /// <summary>
    /// Gets the host application builder.
    /// </summary>
    public IHostApplicationBuilder Builder { get; }

    internal AspireVapeCacheBuilder(IHostApplicationBuilder builder)
    {
        Builder = builder;
    }
}
