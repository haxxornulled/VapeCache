using Autofac;
using Microsoft.AspNetCore.Builder;
using VapeCache.Extensions.Aspire.Autofac;

namespace VapeCache.Extensions.Aspire;

/// <summary>
/// Autofac container composition extensions for Aspire-hosted VapeCache services.
/// </summary>
public static class AspireAutofacExtensions
{
    /// <summary>
    /// Registers VapeCache Autofac modules for core connection/caching composition.
    /// </summary>
    public static AspireVapeCacheBuilder UseAutofacModules(
        this AspireVapeCacheBuilder builder,
        Action<VapeCacheAspireAutofacOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.Builder is not WebApplicationBuilder webBuilder)
            throw new NotSupportedException("UseAutofacModules requires a WebApplicationBuilder host.");

        var options = new VapeCacheAspireAutofacOptions();
        configure?.Invoke(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionName))
            options.ConnectionName = "redis";

        builder.UseTransport(options.TransportMode);

        webBuilder.Host.ConfigureContainer<ContainerBuilder>((_, containerBuilder) =>
        {
            containerBuilder.RegisterModule(
                new VapeCacheAspireAutofacModule(
                    webBuilder.Configuration,
                    options.TransportMode.ToRedisTransportProfile(),
                    options.ConnectionName));
        });

        return builder;
    }
}

public sealed class VapeCacheAspireAutofacOptions
{
    public string ConnectionName { get; set; } = "redis";
    public VapeCacheAspireTransportMode TransportMode { get; set; } = VapeCacheAspireTransportMode.MaxThroughput;
}
