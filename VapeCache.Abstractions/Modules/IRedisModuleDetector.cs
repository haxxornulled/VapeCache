namespace VapeCache.Abstractions.Modules;

/// <summary>
/// Detects which Redis modules are installed on the server.
/// Used to enable advanced features like RedisJSON, RedisBloom, etc.
/// </summary>
public interface IRedisModuleDetector
{
    /// <summary>
    /// Check if a specific module is installed.
    /// Common modules: "ReJSON" (RedisJSON), "bf" (RedisBloom), "search" (RediSearch)
    /// </summary>
    ValueTask<bool> IsModuleInstalledAsync(string moduleName, CancellationToken ct = default);

    /// <summary>
    /// Get all installed module names.
    /// </summary>
    ValueTask<string[]> GetInstalledModulesAsync(CancellationToken ct = default);

    /// <summary>
    /// Check if RedisJSON module is available (enables native JSON operations).
    /// </summary>
    ValueTask<bool> HasRedisJsonAsync(CancellationToken ct = default);
}
