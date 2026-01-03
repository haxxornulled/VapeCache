using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Reconciliation;

public static class RedisReconciliationExtensions
{
    public static IServiceCollection AddVapeCacheRedisReconciliation(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<RedisReconciliationOptions>? configure = null,
        Action<RedisReconciliationStoreOptions>? configureStore = null)
    {
        services.AddOptions<RedisReconciliationOptions>()
            .Configure(o => configuration.GetSection("RedisReconciliation").Bind(o));
        services.AddOptions<RedisReconciliationStoreOptions>()
            .Configure(o => configuration.GetSection("RedisReconciliationStore").Bind(o));

        return services.AddVapeCacheRedisReconciliation(configure, configureStore);
    }

    public static IServiceCollection AddVapeCacheRedisReconciliation(
        this IServiceCollection services,
        Action<RedisReconciliationOptions>? configure = null,
        Action<RedisReconciliationStoreOptions>? configureStore = null)
    {
        var optionsBuilder = services.AddOptions<RedisReconciliationOptions>()
            .Validate(o => o.MaxOperationAge > TimeSpan.Zero, "MaxOperationAge must be greater than zero")
            .Validate(o => o.MaxRunDuration > TimeSpan.Zero, "MaxRunDuration must be greater than zero")
            .Validate(o => o.MaxPendingOperations >= 0, "MaxPendingOperations must be non-negative")
            .Validate(o => o.MaxOperationsPerRun >= 0, "MaxOperationsPerRun must be non-negative")
            .Validate(o => o.BatchSize > 0, "BatchSize must be greater than zero")
            .Validate(o => o.InitialBackoff >= TimeSpan.Zero, "InitialBackoff must be non-negative")
            .Validate(o => o.MaxBackoff >= o.InitialBackoff, "MaxBackoff must be >= InitialBackoff")
            .Validate(o => o.BackoffMultiplier >= 1.0, "BackoffMultiplier must be >= 1.0")
            .ValidateOnStart();

        if (configure is not null)
            optionsBuilder.Configure(configure);

        var storeBuilder = services.AddOptions<RedisReconciliationStoreOptions>()
            .Validate(o => o.BusyTimeoutMs >= 0, "BusyTimeoutMs must be non-negative")
            .ValidateOnStart();

        if (configureStore is not null)
            storeBuilder.Configure(configureStore);

        services.TryAddSingleton<TimeProvider>(_ => TimeProvider.System);
        services.TryAddSingleton<SqliteReconciliationStore>();
        services.TryAddSingleton<InMemoryReconciliationStore>();
        services.TryAddSingleton<IRedisReconciliationExecutor, RedisReconciliationExecutorAdapter>();
        services.TryAddSingleton<IRedisReconciliationStore, RedisReconciliationStoreSelector>();
        services.TryAddSingleton<IRedisReconciliationService, RedisReconciliationService>();

        return services;
    }

    public static IServiceCollection UseSqliteBackingStore(
        this IServiceCollection services,
        Action<RedisReconciliationStoreOptions>? configure = null)
    {
        services.Configure<RedisReconciliationStoreOptions>(o => o.UseSqlite = true);
        if (configure is not null)
            services.Configure(configure);
        return services;
    }

    public static IServiceCollection UseInMemoryBackingStore(
        this IServiceCollection services,
        Action<RedisReconciliationStoreOptions>? configure = null)
    {
        services.Configure<RedisReconciliationStoreOptions>(o => o.UseSqlite = false);
        if (configure is not null)
            services.Configure(configure);
        return services;
    }
}
