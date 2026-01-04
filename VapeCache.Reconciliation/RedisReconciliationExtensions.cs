using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Infrastructure.Connections;
using VapeCache.Licensing;

namespace VapeCache.Reconciliation;

public static class RedisReconciliationExtensions
{
    public static IServiceCollection AddVapeCacheRedisReconciliation(
        this IServiceCollection services,
        IConfiguration configuration,
        string? licenseKey = null,
        Action<RedisReconciliationOptions>? configure = null,
        Action<RedisReconciliationStoreOptions>? configureStore = null)
    {
        services.AddOptions<RedisReconciliationOptions>()
            .Configure(o => configuration.GetSection("RedisReconciliation").Bind(o));
        services.AddOptions<RedisReconciliationStoreOptions>()
            .Configure(o => configuration.GetSection("RedisReconciliationStore").Bind(o));

        return services.AddVapeCacheRedisReconciliation(licenseKey, configure, configureStore);
    }

    public static IServiceCollection AddVapeCacheRedisReconciliation(
        this IServiceCollection services,
        string? licenseKey = null,
        Action<RedisReconciliationOptions>? configure = null,
        Action<RedisReconciliationStoreOptions>? configureStore = null)
    {
        // COMMERCIAL LICENSE VALIDATION - Reconciliation is a paid feature
        // Secret key for HMAC signature verification (would be securely stored in production)
        const string LicenseSecretKey = "VapeCache-HMAC-Secret-2026-Production";
        var validator = new LicenseValidator(LicenseSecretKey);

        // If no license key provided, try to read from environment variable
        licenseKey ??= Environment.GetEnvironmentVariable("VAPECACHE_LICENSE_KEY");

        var validationResult = validator.Validate(licenseKey);

        // Free tier users cannot use reconciliation
        if (validationResult.Tier == LicenseTier.Free)
        {
            throw new VapeCacheLicenseException(
                "VapeCache Reconciliation requires a Pro or Enterprise license. " +
                "This premium feature provides zero-data-loss failover by persisting cache writes during Redis outages. " +
                "Visit https://vapecache.com/pricing to purchase a license or use the free tier without reconciliation.");
        }

        // Validate license is not expired
        if (!validationResult.IsValid)
        {
            throw new VapeCacheLicenseException(
                $"VapeCache license validation failed: {validationResult.ErrorMessage}. " +
                "Visit https://vapecache.com to renew your license.");
        }

        // Pro tier: validate instance count (max 3)
        if (validationResult.Tier == LicenseTier.Pro && validationResult.MaxInstances != 3)
        {
            throw new VapeCacheLicenseException(
                $"VapeCache Pro license is limited to 3 production instances. " +
                "Upgrade to Enterprise for unlimited instances at https://vapecache.com/pricing");
        }

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
