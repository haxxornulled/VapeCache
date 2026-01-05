using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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
        // COMMERCIAL LICENSE VALIDATION - Reconciliation is an ENTERPRISE-ONLY feature
        // Secret key for HMAC signature verification (would be securely stored in production)
        const string LicenseSecretKey = "VapeCache-HMAC-Secret-2026-Production";
        var validator = new LicenseValidator(LicenseSecretKey);

        // If no license key provided, try to read from environment variable
        licenseKey ??= Environment.GetEnvironmentVariable("VAPECACHE_LICENSE_KEY");

        var validationResult = validator.Validate(licenseKey);

        // Only Enterprise tier can use reconciliation
        if (validationResult.Tier != LicenseTier.Enterprise)
        {
            throw new VapeCacheLicenseException(
                "VapeCache Reconciliation is an ENTERPRISE-ONLY feature. " +
                "This premium capability provides zero-data-loss failover with SQLite-backed persistence of cache writes during Redis outages. " +
                "Contact sales at https://vapecache.com/enterprise or use the free tier without reconciliation.");
        }

        // Validate license is not expired
        if (!validationResult.IsValid)
        {
            throw new VapeCacheLicenseException(
                $"VapeCache license validation failed: {validationResult.ErrorMessage}. " +
                "Visit https://vapecache.com to renew your license.");
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

    /// <summary>
    /// Adds the RedisReconciliationReaper background service that automatically runs reconciliation on a schedule.
    /// Call this method after AddVapeCacheRedisReconciliation() to enable automatic background reconciliation.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configure">Optional configuration for the Reaper (interval, initial delay)</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddReconciliationReaper(
        this IServiceCollection services,
        Action<RedisReconciliationReaperOptions>? configure = null)
    {
        var optionsBuilder = services.AddOptions<RedisReconciliationReaperOptions>()
            .Validate(o => o.Interval > TimeSpan.Zero, "Interval must be greater than zero")
            .Validate(o => o.InitialDelay >= TimeSpan.Zero, "InitialDelay must be non-negative")
            .ValidateOnStart();

        if (configure is not null)
            optionsBuilder.Configure(configure);

        services.AddHostedService<RedisReconciliationReaper>();

        return services;
    }

    /// <summary>
    /// Adds the RedisReconciliationReaper background service with configuration from appsettings.json.
    /// Call this method after AddVapeCacheRedisReconciliation() to enable automatic background reconciliation.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">Configuration source (appsettings.json)</param>
    /// <param name="configure">Optional additional configuration</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddReconciliationReaper(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<RedisReconciliationReaperOptions>? configure = null)
    {
        services.AddOptions<RedisReconciliationReaperOptions>()
            .Configure(o => configuration.GetSection("RedisReconciliationReaper").Bind(o));

        return services.AddReconciliationReaper(configure);
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
