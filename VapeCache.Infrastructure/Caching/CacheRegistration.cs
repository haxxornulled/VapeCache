using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
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
    /// <summary>
    /// Adds value.
    /// </summary>
    public static IServiceCollection AddVapecacheCaching(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddOptions<InMemorySpillOptions>();
        services.AddOptions<HybridFailoverOptions>();
        services.AddOptions<RedisMultiplexerOptions>()
            .ValidateOnStart();
        services.TryAddSingleton<RedisMultiplexerOptionsValidator>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<RedisMultiplexerOptions>, RedisMultiplexerOptionsValidator>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, RedisMultiplexerOptionsStartupHostedService>());

        services.AddSingleton<ICurrentCacheService>(sp =>
        {
            var currentCacheService = new CurrentCacheService();
            // Initialize telemetry with current cache service for observable gauge
            CacheTelemetry.Initialize(currentCacheService);
            return currentCacheService;
        });
        services.AddSingleton<CacheStatsRegistry>();
        services.AddSingleton<ICacheStats, CurrentCacheStats>();
        services.AddSingleton<ICacheIntentRegistry, CacheIntentRegistry>();
        services.AddSingleton<TimeProvider>(_ => TimeProvider.System);

        // Core executors (registered as non-interface for internal use)
        services.AddSingleton(sp => new RedisCommandExecutor(
            sp.GetRequiredService<IRedisConnectionFactory>(),
            sp.GetRequiredService<IOptionsMonitor<RedisMultiplexerOptions>>(),
            sp.GetService<IOptionsMonitor<RedisConnectionOptions>>()));
        services.AddSingleton<InMemoryCommandExecutor>();
        services.TryAddSingleton<IRedisFallbackCommandExecutor, InMemoryCommandExecutor>();

        services.TryAddSingleton<IInMemorySpillStore, NoopSpillStore>();
        services.TryAddSingleton<ISpillStoreDiagnostics>(sp =>
        {
            var spillStore = sp.GetRequiredService<IInMemorySpillStore>();
            return spillStore as ISpillStoreDiagnostics ?? FallbackSpillDiagnostics.Instance;
        });

        // Cache services
        // IMPORTANT: RedisCacheService gets the RAW RedisCommandExecutor (no hybrid wrapper)
        // to avoid circular dependency with HybridCacheService
        services.AddSingleton(sp => new RedisCacheService(
            sp.GetRequiredService<RedisCommandExecutor>(),
            sp.GetRequiredService<ICurrentCacheService>(),
            sp.GetRequiredService<CacheStatsRegistry>(),
            sp.GetRequiredService<ICacheIntentRegistry>()));
        services.AddSingleton<InMemoryCacheService>();
        services.TryAddSingleton<ICacheFallbackService, InMemoryCacheService>();
        services.AddSingleton<HybridCacheService>();
        services.AddSingleton<IRedisCircuitBreakerState>(sp => sp.GetRequiredService<HybridCacheService>());
        services.AddSingleton<IRedisFailoverController>(sp => sp.GetRequiredService<HybridCacheService>());

        // Hybrid command executor - automatically switches between Redis and in-memory based on circuit breaker state
        services.AddSingleton<IRedisCommandExecutor, HybridCommandExecutor>();
        services.AddSingleton<IRedisMultiplexerDiagnostics>(sp => sp.GetRequiredService<RedisCommandExecutor>());

        services.TryAddSingleton<CacheStampedeOptions>();

        // Circuit breaker options with validation
        services.AddOptions<RedisCircuitBreakerOptions>()
            .Validate(o => o.ConsecutiveFailuresToOpen >= 1, "ConsecutiveFailuresToOpen must be at least 1")
            .Validate(o => o.BreakDuration > TimeSpan.Zero, "BreakDuration must be greater than zero")
            .Validate(o => o.HalfOpenProbeTimeout > TimeSpan.Zero, "HalfOpenProbeTimeout must be greater than zero to prevent indefinite hangs")
            .ValidateOnStart();
        services.AddOptions<CacheStampedeOptions>()
            .UseCacheStampedeProfile(CacheStampedeProfile.Balanced)
            .Validate(o => o.MaxKeys > 0, "MaxKeys must be greater than zero.")
            .Validate(o => o.MaxKeys <= 500_000, "MaxKeys must be less than or equal to 500000.")
            .Validate(o => o.MaxKeyLength > 0, "MaxKeyLength must be greater than zero.")
            .Validate(o => o.MaxKeyLength <= 4096, "MaxKeyLength must be less than or equal to 4096.")
            .Validate(o => o.LockWaitTimeout >= TimeSpan.Zero, "LockWaitTimeout must be greater than or equal to zero.")
            .Validate(o => o.LockWaitTimeout <= TimeSpan.FromSeconds(30), "LockWaitTimeout must be less than or equal to 30 seconds.")
            .Validate(o => o.FailureBackoff >= TimeSpan.Zero, "FailureBackoff must be greater than or equal to zero.")
            .Validate(o => o.FailureBackoff <= TimeSpan.FromSeconds(30), "FailureBackoff must be less than or equal to 30 seconds.")
            .ValidateOnStart();
        services.AddOptions<HybridFailoverOptions>()
            .Validate(o => o.FallbackWarmReadTtl > TimeSpan.Zero, "FallbackWarmReadTtl must be greater than zero.")
            .Validate(o => o.FallbackMirrorWriteTtlWhenMissing > TimeSpan.Zero, "FallbackMirrorWriteTtlWhenMissing must be greater than zero.")
            .Validate(o => o.MaxMirrorPayloadBytes >= 0, "MaxMirrorPayloadBytes must be greater than or equal to zero.")
            .ValidateOnStart();
        // Default cache service is the hybrid implementation with stampede protection applied directly.
        services.AddSingleton<StampedeProtectedCacheService>(sp => new StampedeProtectedCacheService(
            sp.GetRequiredService<HybridCacheService>(),
            sp.GetRequiredService<IOptionsMonitor<CacheStampedeOptions>>(),
            sp.GetRequiredService<CacheStatsRegistry>().GetOrCreate(CacheStatsNames.Hybrid)));
        services.AddSingleton<ICacheService>(sp => sp.GetRequiredService<StampedeProtectedCacheService>());
        services.AddSingleton<ICacheTagService>(sp => sp.GetRequiredService<StampedeProtectedCacheService>());

        // Ergonomic typed caching API with codec-based serialization
        services.TryAddSingleton<ICacheCodecProvider>(sp =>
            new SystemTextJsonCodecProvider(new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        services.AddSingleton<IVapeCache, VapeCacheClient>();
        services.AddSingleton<IJsonCache, JsonCacheService>();
        services.AddSingleton<ICacheChunkStreamService, ChunkedCacheStreamService>();

        // Typed collection APIs (LIST, SET, HASH)
        services.AddSingleton<ICacheCollectionFactory, CacheCollectionFactory>();

        // Redis module detection (for RedisJSON, RediSearch, etc.)
        services.AddSingleton<IRedisModuleDetector>(sp =>
            new RedisModuleDetector(sp.GetRequiredService<RedisCommandExecutor>()));
        services.AddSingleton<IRedisSearchService, RedisSearchService>();
        services.AddSingleton<IRedisBloomService, RedisBloomService>();
        services.AddSingleton<IRedisTimeSeriesService, RedisTimeSeriesService>();

        return services;
    }

    private sealed class FallbackSpillDiagnostics : ISpillStoreDiagnostics
    {
        public static readonly FallbackSpillDiagnostics Instance = new();

        public SpillStoreDiagnosticsSnapshot GetSnapshot()
            => new(
                SupportsDiskSpill: false,
                SpillToDiskConfigured: false,
                Mode: "unknown",
                TotalSpillFiles: 0,
                ActiveShards: 0,
                MaxFilesInShard: 0,
                AvgFilesPerActiveShard: 0d,
                ImbalanceRatio: 0d,
                TopShards: Array.Empty<SpillShardLoad>(),
                SampledAtUtc: DateTimeOffset.UtcNow);
    }
}
