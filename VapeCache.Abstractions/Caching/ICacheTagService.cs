namespace VapeCache.Abstractions.Caching;

/// <summary>
/// Provides tag-based invalidation primitives for cache entries.
/// Tagged entries remain in cache storage, but become immediately stale
/// when their tag version is advanced.
/// </summary>
public interface ICacheTagService
{
    /// <summary>
    /// Advances the version for a tag and invalidates all entries bound to it.
    /// Returns the new version value.
    /// </summary>
    ValueTask<long> InvalidateTagAsync(string tag, CancellationToken ct = default);

    /// <summary>
    /// Gets the current version for a tag.
    /// </summary>
    ValueTask<long> GetTagVersionAsync(string tag, CancellationToken ct = default);

    /// <summary>
    /// Advances the version for a cache zone and invalidates all entries in that zone.
    /// Zones are implemented as reserved tag names.
    /// Returns the new version value.
    /// </summary>
    ValueTask<long> InvalidateZoneAsync(string zone, CancellationToken ct = default);

    /// <summary>
    /// Gets the current version for a cache zone.
    /// </summary>
    ValueTask<long> GetZoneVersionAsync(string zone, CancellationToken ct = default);
}
