using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VapeCache.Guards;

namespace VapeCache.Extensions.EntityFrameworkCore;

/// <summary>
/// IServiceCollection extensions for EF Core second-level cache interceptor wiring.
/// </summary>
public static class VapeCacheEfCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers EF Core second-level cache interceptor contracts and default implementations.
    /// </summary>
    public static IServiceCollection AddVapeCacheEntityFrameworkCore(
        this IServiceCollection services,
        Action<EfCoreSecondLevelCacheOptions>? configure = null)
    {
        ParanoiaThrowGuard.Against.NotNull(services);

        services.AddOptions<EfCoreSecondLevelCacheOptions>()
            .Validate(
                static o => !string.IsNullOrWhiteSpace(o.ZonePrefix),
                "ZonePrefix is required.")
            .ValidateOnStart();

        if (configure is not null)
            services.Configure(configure);

        services.TryAddSingleton<IEfCoreQueryCacheKeyBuilder, Sha256EfCoreQueryCacheKeyBuilder>();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IInterceptor, VapeCacheEfCoreCommandInterceptor>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IInterceptor, VapeCacheEfCoreSaveChangesInterceptor>());

        return services;
    }
}
