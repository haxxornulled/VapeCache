namespace VapeCache.Abstractions.Connections;

/// <summary>
/// Named transport tuning profiles for Redis socket/framing behavior.
/// </summary>
public enum RedisTransportProfile
{
    /// <summary>
    /// Use explicit option values as configured.
    /// </summary>
    Custom = 0,

    /// <summary>
    /// Maximum throughput profile (default).
    /// </summary>
    FullTilt = 1,

    /// <summary>
    /// Balanced throughput/latency profile.
    /// </summary>
    Balanced = 2,

    /// <summary>
    /// Lower latency profile with smaller batching.
    /// </summary>
    LowLatency = 3
}
