using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VapeCache.Extensions.EntityFrameworkCore;
using VapeCache.Guards;

namespace VapeCache.Extensions.EntityFrameworkCore.OpenTelemetry;

/// <summary>
/// IServiceCollection extensions for EF Core cache OpenTelemetry wiring.
/// </summary>
public static class EfCoreOpenTelemetryServiceCollectionExtensions
{
    /// <summary>
    /// Adds OpenTelemetry observer wiring for EF Core cache interceptor events.
    /// </summary>
    public static IServiceCollection AddVapeCacheEfCoreOpenTelemetry(
        this IServiceCollection services,
        Action<EfCoreOpenTelemetryOptions>? configure = null)
    {
        ParanoiaThrowGuard.Against.NotNull(services);

        services.AddOptions<EfCoreOpenTelemetryOptions>();
        if (configure is not null)
            services.Configure(configure);

        // Ensure interceptor observers are active when this package is used.
        services.PostConfigure<EfCoreSecondLevelCacheOptions>(
            static options => options.EnableObserverCallbacks = true);

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IEfCoreSecondLevelCacheObserver, EfCoreOpenTelemetryObserver>());

        return services;
    }
}
