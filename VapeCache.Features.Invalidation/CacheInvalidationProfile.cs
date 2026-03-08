namespace VapeCache.Features.Invalidation;

/// <summary>
/// Opinionated runtime profile presets for invalidation execution.
/// </summary>
public enum CacheInvalidationProfile
{
    /// <summary>
    /// Best-effort, low-overhead behavior intended for smaller web workloads.
    /// </summary>
    SmallWebsite = 0,

    /// <summary>
    /// Strict, higher-concurrency behavior intended for larger traffic workloads.
    /// </summary>
    HighTrafficSite = 1,

    /// <summary>
    /// Conservative, low-noise behavior for desktop and client applications.
    /// </summary>
    DesktopApp = 2
}
