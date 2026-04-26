using Microsoft.Extensions.Configuration;

namespace VapeCache.Extensions.Aspire.Hosting;

/// <summary>
/// Applies shared local host defaults so UI and stress hosts use the same runtime shape
/// unless a caller explicitly overrides values through configuration.
/// </summary>
public static class VapeCacheRuntimeHostDefaults
{
    /// <summary>
    /// Applies the standard Redis multiplexer defaults used by local VapeCache hosts.
    /// Existing configuration values always win.
    /// </summary>
    public static void ApplyRedisMultiplexerDefaults(IConfigurationManager configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        ApplyIfMissing(configuration, "RedisMultiplexer:Connections", "64");
        ApplyIfMissing(configuration, "RedisMultiplexer:EnableAutoscaling", "true");
        ApplyIfMissing(configuration, "RedisMultiplexer:MinConnections", "16");
        ApplyIfMissing(configuration, "RedisMultiplexer:MaxConnections", "64");
        ApplyIfMissing(configuration, "RedisMultiplexer:BulkLaneConnections", "16");
        ApplyIfMissing(configuration, "RedisMultiplexer:AutoAdjustBulkLanes", "true");
        ApplyIfMissing(configuration, "RedisMultiplexer:BulkLaneTargetRatio", "0.25");
    }

    private static void ApplyIfMissing(IConfiguration configuration, string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(configuration[key]))
            return;

        configuration[key] = value;
    }
}
