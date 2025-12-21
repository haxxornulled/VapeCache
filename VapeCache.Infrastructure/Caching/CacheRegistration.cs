using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VapeCache.Application.Caching;
using VapeCache.Application.Connections;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Infrastructure.Caching;

public static class CacheRegistration
{
    public static IServiceCollection AddVapecacheCaching(this IServiceCollection services)
    {
        services.AddMemoryCache();

        services.AddSingleton<ICurrentCacheService, CurrentCacheService>();
        services.AddSingleton<CacheStats>();
        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CacheStats>());
        services.AddSingleton<TimeProvider>(_ => TimeProvider.System);

        services.AddSingleton<IRedisCommandExecutor, RedisCommandExecutor>();
        services.AddSingleton<RedisCacheService>();
        services.AddSingleton<InMemoryCacheService>();
        services.AddSingleton<HybridCacheService>();
        services.AddSingleton<IRedisCircuitBreakerState>(sp => sp.GetRequiredService<HybridCacheService>());

        services.TryAddSingleton<CacheStampedeOptions>();
        services.TryAddSingleton<RedisCircuitBreakerOptions>();

        // Default cache service is the hybrid implementation, wrapped with stampede protection.
        services.AddSingleton<ICacheService>(sp =>
        {
            var hybrid = sp.GetRequiredService<HybridCacheService>();
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CacheStampedeOptions>>();
            return new StampedeProtectedCacheService(hybrid, options);
        });
        return services;
    }
}
