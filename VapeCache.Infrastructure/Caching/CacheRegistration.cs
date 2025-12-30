using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Collections;
using VapeCache.Abstractions.Connections;
using VapeCache.Abstractions.Modules;
using VapeCache.Infrastructure.Caching.Codecs;
using VapeCache.Infrastructure.Collections;
using VapeCache.Infrastructure.Connections;
using VapeCache.Infrastructure.Modules;

namespace VapeCache.Infrastructure.Caching;

public static class CacheRegistration
{
    public static IServiceCollection AddVapecacheCaching(this IServiceCollection services)
    {
        services.AddMemoryCache();

        services.AddSingleton<ICurrentCacheService>(sp =>
        {
            var currentCacheService = new CurrentCacheService();
            // Initialize telemetry with current cache service for observable gauge
            CacheTelemetry.Initialize(currentCacheService);
            return currentCacheService;
        });
        services.AddSingleton<CacheStatsRegistry>();
        services.AddSingleton<ICacheStats, CurrentCacheStats>();
        services.AddSingleton<TimeProvider>(_ => TimeProvider.System);

        // Core executors (registered as non-interface for internal use)
        services.AddSingleton<RedisCommandExecutor>(sp =>
        {
            var factory = sp.GetRequiredService<RedisConnectionFactory>();
            var muxOptions = sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<RedisMultiplexerOptions>>();
            var connOptions = sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<RedisConnectionOptions>>();
            return new RedisCommandExecutor(factory, muxOptions, connOptions);
        });
        services.AddSingleton<InMemoryCommandExecutor>();

        // Cache services
        // IMPORTANT: RedisCacheService gets the RAW RedisCommandExecutor (no hybrid wrapper)
        // to avoid circular dependency with HybridCacheService
        services.AddSingleton<RedisCacheService>(sp =>
        {
            var redis = sp.GetRequiredService<RedisCommandExecutor>();
            var current = sp.GetRequiredService<ICurrentCacheService>();
            var statsRegistry = sp.GetRequiredService<CacheStatsRegistry>();
            return new RedisCacheService(redis, current, statsRegistry);
        });
        services.AddSingleton<InMemoryCacheService>();
        services.AddSingleton<HybridCacheService>();
        services.AddSingleton<IRedisCircuitBreakerState>(sp => sp.GetRequiredService<HybridCacheService>());
        services.AddSingleton<IRedisFailoverController>(sp => sp.GetRequiredService<HybridCacheService>());

        // Hybrid command executor - automatically switches between Redis and in-memory based on circuit breaker state
        services.AddSingleton<IRedisCommandExecutor, HybridCommandExecutor>();

        services.TryAddSingleton<CacheStampedeOptions>();

        // Circuit breaker options with validation
        services.AddOptions<RedisCircuitBreakerOptions>()
            .Validate(o => o.ConsecutiveFailuresToOpen >= 1, "ConsecutiveFailuresToOpen must be at least 1")
            .Validate(o => o.BreakDuration > TimeSpan.Zero, "BreakDuration must be greater than zero")
            .Validate(o => o.HalfOpenProbeTimeout > TimeSpan.Zero, "HalfOpenProbeTimeout must be greater than zero to prevent indefinite hangs")
            .ValidateOnStart();
        services.TryAddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RedisCircuitBreakerOptions>>().Value);

        // Reconciliation service - syncs in-memory writes back to Redis after recovery
        services.AddOptions<RedisReconciliationOptions>()
            .Validate(o => o.MaxOperationAge > TimeSpan.Zero, "MaxOperationAge must be greater than zero")
            .Validate(o => o.MaxRunDuration > TimeSpan.Zero, "MaxRunDuration must be greater than zero")
            .Validate(o => o.MaxBatchSize >= 0, "MaxBatchSize must be non-negative")
            .ValidateOnStart();
        services.TryAddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RedisReconciliationOptions>>().Value);
        services.AddSingleton<IRedisReconciliationService>(sp =>
        {
            var executor = sp.GetRequiredService<RedisCommandExecutor>(); // raw executor (no circuit breaker)
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RedisReconciliationOptions>>();
            var logger = sp.GetRequiredService<ILogger<RedisReconciliationService>>();
            return new RedisReconciliationService(executor, options, logger);
        });

        // Default cache service is the hybrid implementation, wrapped with stampede protection.
        services.AddSingleton<ICacheService>(sp =>
        {
            var hybrid = sp.GetRequiredService<HybridCacheService>();
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CacheStampedeOptions>>();
            return new StampedeProtectedCacheService(hybrid, options);
        });

        // Ergonomic typed caching API with codec-based serialization
        services.TryAddSingleton<ICacheCodecProvider>(sp =>
            new SystemTextJsonCodecProvider(new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        services.AddSingleton<IVapeCache, VapeCacheClient>();

        // Typed collection APIs (LIST, SET, HASH)
        services.AddSingleton<ICacheCollectionFactory, CacheCollectionFactory>();

        // Redis module detection (for RedisJSON, RediSearch, etc.)
        services.AddSingleton<IRedisModuleDetector, RedisModuleDetector>();

        return services;
    }
}
