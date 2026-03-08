using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using VapeCache.Extensions.Aspire.Hosting;

namespace VapeCache.Extensions.Aspire;

/// <summary>
/// Startup warmup and readiness extensions for Aspire-hosted VapeCache services.
/// </summary>
public static class AspireStartupWarmupExtensions
{
    /// <summary>
    /// Enables Redis connection-pool warmup during startup and tracks readiness state.
    /// </summary>
    public static AspireVapeCacheBuilder WithStartupWarmup(
        this AspireVapeCacheBuilder builder,
        Action<VapeCacheStartupWarmupOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Builder.Services.AddOptions<VapeCacheStartupWarmupOptions>();
        builder.Builder.Services.PostConfigure<VapeCacheStartupWarmupOptions>(options =>
        {
            if (!options.Enabled)
                options.Enabled = true;
        });

        if (configure is not null)
            builder.Builder.Services.Configure(configure);

        builder.Builder.Services.TryAddSingleton<IVapeCacheStartupReadiness, VapeCacheStartupReadinessState>();
        builder.Builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, VapeCacheStartupWarmupHostedService>());

        return builder;
    }
}
