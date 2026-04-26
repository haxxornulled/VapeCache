using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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

        builder.Builder.Services.TryAddSingleton<VapeCacheLiveMetricsFeed>();
        builder.Builder.Services.TryAddSingleton<IVapeCacheLiveMetricsFeed>(static sp => sp.GetRequiredService<VapeCacheLiveMetricsFeed>());
        builder.Builder.Services.AddSingleton<IHostedService>(static sp => sp.GetRequiredService<VapeCacheLiveMetricsFeed>());
        builder.Builder.Services.AddSingleton<IHostedLifecycleService>(static sp => sp.GetRequiredService<VapeCacheLiveMetricsFeed>());
        builder.Builder.Services.TryAddSingleton<VapeCacheSharedDashboardSnapshotPublisher>();
        builder.Builder.Services.AddSingleton<IHostedService>(static sp => sp.GetRequiredService<VapeCacheSharedDashboardSnapshotPublisher>());
        builder.Builder.Services.AddSingleton<IHostedLifecycleService>(static sp => sp.GetRequiredService<VapeCacheSharedDashboardSnapshotPublisher>());

        builder.Builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IStartupFilter, VapeCacheEndpointStartupFilter>());

        return builder;
    }

    private sealed class VapeCacheEndpointStartupFilter : IStartupFilter
    {
        private readonly IOptions<VapeCacheEndpointOptions> options;

        public VapeCacheEndpointStartupFilter(IOptions<VapeCacheEndpointOptions> options)
        {
            this.options = options;
        }

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

                if (app is IEndpointRouteBuilder endpointRouteBuilder)
                {
                    endpointRouteBuilder.MapVapeCacheEndpoints(
                        endpointOptions.Prefix,
                        includeBreakerControlEndpoints: false,
                        endpointOptions.EnableLiveStream,
                        endpointOptions.IncludeIntentEndpoints,
                        endpointOptions.EnableDashboard);

                    if (endpointOptions.IncludeBreakerControlEndpoints)
                    {
                        endpointRouteBuilder.MapVapeCacheAdminEndpoints(
                            endpointOptions.AdminPrefix,
                            endpointOptions.RequireAuthorizationOnAdminEndpoints,
                            endpointOptions.AdminAuthorizationPolicy);
                    }

                    return;
                }

                app.UseRouting();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapVapeCacheEndpoints(
                        endpointOptions.Prefix,
                        includeBreakerControlEndpoints: false,
                        endpointOptions.EnableLiveStream,
                        endpointOptions.IncludeIntentEndpoints,
                        endpointOptions.EnableDashboard);

                    if (endpointOptions.IncludeBreakerControlEndpoints)
                    {
                        endpoints.MapVapeCacheAdminEndpoints(
                            endpointOptions.AdminPrefix,
                            endpointOptions.RequireAuthorizationOnAdminEndpoints,
                            endpointOptions.AdminAuthorizationPolicy);
                    }
                });
            };
        }
    }
}
