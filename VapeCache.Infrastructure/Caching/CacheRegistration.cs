using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        services.AddOptions<InMemorySpillOptions>();

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
        services.AddSingleton<RedisCommandExecutor>();
        services.AddSingleton<InMemoryCommandExecutor>();
        services.TryAddSingleton<IRedisFallbackCommandExecutor, InMemoryCommandExecutor>();

        services.TryAddSingleton<ISpillEncryptionProvider, NoopSpillEncryptionProvider>();
        services.TryAddSingleton<IInMemorySpillStore, FileSpillStore>();

        // Cache services
        // IMPORTANT: RedisCacheService gets the RAW RedisCommandExecutor (no hybrid wrapper)
        // to avoid circular dependency with HybridCacheService
        services.AddSingleton<RedisCacheService>();
        services.AddSingleton<InMemoryCacheService>();
        services.TryAddSingleton<ICacheFallbackService, InMemoryCacheService>();
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
        // Default cache service is the hybrid implementation, wrapped with stampede protection.
        services.AddSingleton<ICacheService, HybridStampedeCacheService>();

        // Ergonomic typed caching API with codec-based serialization
        services.TryAddSingleton<ICacheCodecProvider>(sp =>
            new SystemTextJsonCodecProvider(new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        services.AddSingleton<IVapeCache, VapeCacheClient>();
        services.AddSingleton<IJsonCache, JsonCacheService>();

        // Typed collection APIs (LIST, SET, HASH)
        services.AddSingleton<ICacheCollectionFactory, CacheCollectionFactory>();

        // Redis module detection (for RedisJSON, RediSearch, etc.)
        services.AddSingleton<IRedisModuleDetector, RedisModuleDetector>();
        services.AddSingleton<IRedisSearchService, RedisSearchService>();
        services.AddSingleton<IRedisBloomService, RedisBloomService>();
        services.AddSingleton<IRedisTimeSeriesService, RedisTimeSeriesService>();

        return services;
    }
}
