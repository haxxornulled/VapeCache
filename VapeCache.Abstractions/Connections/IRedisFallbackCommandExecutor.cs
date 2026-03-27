namespace VapeCache.Abstractions.Connections;

/// <summary>
/// Defines a fallback Redis executor used when Redis is unavailable.
/// </summary>
public interface IRedisFallbackCommandExecutor : IRedisCommandExecutor
{
    /// <summary>
    /// Logical name for telemetry (e.g. "memory", "disk", "proxy").
    /// </summary>
    string Name { get; }
}
