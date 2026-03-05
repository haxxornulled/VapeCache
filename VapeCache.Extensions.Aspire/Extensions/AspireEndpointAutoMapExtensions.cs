using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace VapeCache.Extensions.Aspire;

/// <summary>
/// Extension methods for automatic endpoint mapping so hosts do not need to map each VapeCache route in Program.cs.
/// </summary>
public static class AspireEndpointAutoMapExtensions
{
    /// <summary>
    /// Registers automatic mapping for VapeCache endpoint surface.
    /// </summary>
    /// <param name="builder">The VapeCache builder.</param>
    /// <param name="configure">Optional endpoint options configuration.</param>
    /// <returns>The builder for method chaining.</returns>
    public static AspireVapeCacheBuilder WithAutoMappedEndpoints(
        this AspireVapeCacheBuilder builder,
        Action<VapeCacheEndpointOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Builder.Services.AddOptions<VapeCacheEndpointOptions>();
        if (configure is not null)
            builder.Builder.Services.Configure(configure);

        builder.Builder.Services.AddSingleton<IVapeCacheLiveMetricsFeed, VapeCacheLiveMetricsFeed>();
        builder.Builder.Services.AddHostedService(sp => (VapeCacheLiveMetricsFeed)sp.GetRequiredService<IVapeCacheLiveMetricsFeed>());

        builder.Builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IStartupFilter, VapeCacheEndpointStartupFilter>());

        return builder;
    }

    private sealed class VapeCacheEndpointStartupFilter(IOptions<VapeCacheEndpointOptions> options)
        : IStartupFilter
    {
        /// <summary>
        /// Executes value.
        /// </summary>
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                next(app);

                var endpointOptions = options.Value;
                if (!endpointOptions.Enabled)
                    return;

                app.UseRouting();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapVapeCacheEndpoints(
                        endpointOptions.Prefix,
                        endpointOptions.IncludeBreakerControlEndpoints,
                        endpointOptions.EnableLiveStream,
                        endpointOptions.IncludeIntentEndpoints,
                        endpointOptions.EnableDashboard);
                });
            };
        }
    }
}
