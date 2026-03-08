namespace VapeCache.Abstractions.Caching;

/// <summary>
/// High-level, type-safe caching API that provides an ergonomic interface
/// over the low-level ICacheService infrastructure.
///
/// This is the primary application-facing caching interface that hides
/// serialization complexity behind codec providers while maintaining
/// zero-allocation performance characteristics on hot paths.
/// </summary>
public interface IVapeCache
{
    /// <summary>
    /// Creates or retrieves a cache region for organizing related cache entries.
    /// Regions provide automatic key prefixing to avoid collisions.
    /// </summary>
    /// <param name="name">The region name (used as key prefix).</param>
    /// <returns>A cache region interface for the specified namespace.</returns>
    ICacheRegion Region(string name);

    /// <summary>
    /// Gets a cached value by typed key.
    /// Returns default(T) if the key doesn't exist or deserialization fails.
    /// </summary>
    ValueTask<T?> GetAsync<T>(CacheKey<T> key, CancellationToken ct = default);

    /// <summary>
    /// Sets a value in the cache with the specified options.
    /// The value will be serialized using the registered codec for type T.
    /// </summary>
    ValueTask SetAsync<T>(CacheKey<T> key, T value, CacheEntryOptions options = default, CancellationToken ct = default);

    /// <summary>
    /// Gets a cached value or creates it using the provided factory function.
    /// This is the primary cache access pattern for read-heavy workloads.
    ///
    /// If stampede protection is enabled in options, concurrent requests for the
    /// same key will be coalesced to a single factory invocation.
    /// </summary>
    /// <typeparam name="T">The type of cached value.</typeparam>
    /// <param name="key">Typed cache key.</param>
    /// <param name="factory">Factory function to create the value if not cached.</param>
    /// <param name="options">Cache entry options (TTL, stampede protection, etc.).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The cached or newly created value.</returns>
    ValueTask<T> GetOrCreateAsync<T>(
        CacheKey<T> key,
        Func<CancellationToken, ValueTask<T>> factory,
        CacheEntryOptions options = default,
        CancellationToken ct = default);

    /// <summary>
    /// Removes a cache entry.
    /// Works with both typed and untyped cache keys.
    /// </summary>
    /// <param name="key">The cache key to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the entry was removed, false if it didn't exist.</returns>
    ValueTask<bool> RemoveAsync(CacheKey key, CancellationToken ct = default);

    /// <summary>
    /// Advances the version for a tag and invalidates all entries bound to it.
    /// </summary>
    ValueTask<long> InvalidateTagAsync(string tag, CancellationToken ct = default);

    /// <summary>
    /// Gets the current version for a tag.
    /// </summary>
    ValueTask<long> GetTagVersionAsync(string tag, CancellationToken ct = default);

    /// <summary>
    /// Advances the version for a cache zone and invalidates all entries in that zone.
    /// </summary>
    ValueTask<long> InvalidateZoneAsync(string zone, CancellationToken ct = default);

    /// <summary>
    /// Gets the current version for a cache zone.
    /// </summary>
    ValueTask<long> GetZoneVersionAsync(string zone, CancellationToken ct = default);
}
