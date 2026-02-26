namespace VapeCache.Abstractions.Caching;

/// <summary>
/// Controls how the hybrid cache keeps local in-memory fallback state warm while Redis is healthy.
/// These settings improve failover continuity during Redis incidents.
/// </summary>
public sealed class HybridFailoverOptions
{
    /// <summary>
    /// Mirrors successful Redis writes into the local fallback cache.
    /// This improves continuity when the circuit opens shortly after a write.
    /// </summary>
    public bool MirrorWritesToFallbackWhenRedisHealthy { get; set; } = true;

    /// <summary>
    /// Mirrors successful Redis read hits into the local fallback cache.
    /// This improves continuity for hot keys during failover.
    /// </summary>
    public bool WarmFallbackOnRedisReadHit { get; set; } = true;

    /// <summary>
    /// TTL used when warming fallback from Redis reads.
    /// </summary>
    public TimeSpan FallbackWarmReadTtl { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// TTL used when mirroring writes that do not specify a cache-entry TTL.
    /// </summary>
    public TimeSpan FallbackMirrorWriteTtlWhenMissing { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum payload size eligible for fallback warming/mirroring.
    /// Set to 0 to disable size limits.
    /// </summary>
    public int MaxMirrorPayloadBytes { get; set; } = 256 * 1024;

    /// <summary>
    /// Removes local fallback entries when Redis reports a miss for the same key.
    /// This avoids serving stale fallback data during subsequent outages.
    /// </summary>
    public bool RemoveStaleFallbackOnRedisMiss { get; set; } = true;
}
