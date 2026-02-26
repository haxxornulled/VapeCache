using Microsoft.Extensions.Configuration;

namespace VapeCache.Extensions.Aspire;

public static class AspireRedisResourceExtensions
{
    /// <summary>
    /// Configures value.
    /// </summary>
    public static AspireVapeCacheBuilder WithRedisFromAspire(
        this AspireVapeCacheBuilder builder,
        string connectionName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);

        // Aspire automatically injects connection strings when .WithReference(redis) is called.
        // The connection string is available at ConnectionStrings:{connectionName}
        //
        // VapeCache.Infrastructure already reads ConnectionString from RedisConnectionOptions,
        // and the host should bind it from configuration:
        //
        // builder.Services.Configure<RedisConnectionOptions>(
        //     builder.Configuration.GetSection("RedisConnection"));
        //
        // For Aspire, users should set VAPECACHE_REDIS_CONNECTIONSTRING environment variable
        // or configure RedisConnectionOptions.ConnectionString in appsettings.json to reference
        // the Aspire-injected connection string.
        //
        // This method is provided for future enhancements and documentation purposes.

        return builder;
    }
}
