namespace VapeCache.Abstractions.Caching;

/// <summary>
/// Named stampede-protection profiles for fast, consistent startup configuration.
/// </summary>
public enum CacheStampedeProfile
{
    /// <summary>
    /// Tighter wait windows and stronger backoff for origin protection.
    /// </summary>
    Strict = 1,

    /// <summary>
    /// Recommended default profile for most production services.
    /// </summary>
    Balanced = 2,

    /// <summary>
    /// Looser waits and lower backoff where throughput is preferred over strict origin shielding.
    /// </summary>
    Relaxed = 3
}
