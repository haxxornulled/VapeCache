// ========================= File: Vapecache.Infrastructure/Connections/RedisConnectionRegistration.cs =========================
using Microsoft.Extensions.DependencyInjection;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Infrastructure.Connections;

public static class RedisConnectionRegistration
{
    public static IServiceCollection AddVapecacheRedisConnections(this IServiceCollection services)
    {
        services.AddSingleton<IRedisConnectionFactory, RedisConnectionFactory>();
        services.AddSingleton<RedisConnectionPool>();
        services.AddSingleton<IRedisConnectionPool>(sp => sp.GetRequiredService<RedisConnectionPool>());
        services.AddSingleton<IRedisConnectionPoolReaper>(sp => sp.GetRequiredService<RedisConnectionPool>());
        return services;
    }
}
