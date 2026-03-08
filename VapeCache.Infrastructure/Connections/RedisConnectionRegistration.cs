// ========================= File: Vapecache.Infrastructure/Connections/RedisConnectionRegistration.cs =========================
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Infrastructure.Connections;

public static class RedisConnectionRegistration
{
    /// <summary>
    /// Adds value.
    /// </summary>
    public static IServiceCollection AddVapecacheRedisConnections(this IServiceCollection services)
    {
        RedisTelemetry.EnsureInitialized();

        services.AddOptions<RedisConnectionOptions>()
            .ValidateOnStart();
        services.TryAddSingleton<RedisConnectionOptionsValidator>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<RedisConnectionOptions>, RedisConnectionOptionsValidator>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, RedisConnectionOptionsStartupHostedService>());

        // Register the raw factory first (without circuit breaker)
        services.AddSingleton<RedisConnectionFactory>();

        // Wrap it with circuit breaker using Polly
        // Reconciliation service is optional and injected if available
        services.AddSingleton<IRedisConnectionFactory, CircuitBreakerRedisConnectionFactory>();

        services.AddSingleton<RedisConnectionPool>();
        services.AddSingleton<IRedisConnectionPool>(sp => sp.GetRequiredService<RedisConnectionPool>());
        services.AddSingleton<IRedisConnectionPoolReaper>(sp => sp.GetRequiredService<RedisConnectionPool>());
        return services;
    }
}
