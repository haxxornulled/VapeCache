using VapeCache.Abstractions.Connections;

namespace VapeCache.Extensions.Aspire;

/// <summary>
/// High-level Aspire transport mode selection for VapeCache Redis data plane behavior.
/// </summary>
public enum VapeCacheAspireTransportMode
{
    /// <summary>
    /// Optimized for peak throughput under sustained load.
    /// </summary>
    MaxThroughput = 1,

    /// <summary>
    /// Balanced throughput and latency profile for general workloads.
    /// </summary>
    Balanced = 2,

    /// <summary>
    /// Optimized for lower tail latency with tighter batching.
    /// </summary>
    UltraLowLatency = 3
}

internal static class VapeCacheAspireTransportModeExtensions
{
    public static RedisTransportProfile ToRedisTransportProfile(this VapeCacheAspireTransportMode mode)
        => mode switch
        {
            VapeCacheAspireTransportMode.MaxThroughput => RedisTransportProfile.FullTilt,
            VapeCacheAspireTransportMode.Balanced => RedisTransportProfile.Balanced,
            VapeCacheAspireTransportMode.UltraLowLatency => RedisTransportProfile.LowLatency,
            _ => RedisTransportProfile.FullTilt
        };
}

