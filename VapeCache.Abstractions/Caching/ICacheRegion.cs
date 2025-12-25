namespace VapeCache.Abstractions.Caching;

/// <summary>
/// Represents a logical cache region (namespace) for organizing related cache entries.
/// Regions automatically prefix cache keys to avoid collisions between different
/// functional areas of the application.
/// </summary>
public interface ICacheRegion
{
    /// <summary>
    /// The name of this cache region, used as a prefix for all keys.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Creates a typed cache key within this region by combining the region name with the provided ID.
    /// </summary>
    /// <typeparam name="T">The type of value this key represents.</typeparam>
    /// <param name="id">The unique identifier within this region.</param>
    /// <returns>A typed cache key in the format "{RegionName}:{id}".</returns>
    CacheKey<T> Key<T>(string id);

    /// <summary>
    /// Gets a cached value or creates it using the provided factory function.
    /// This is the primary cache access pattern for read-heavy workloads.
    /// </summary>
    /// <typeparam name="T">The type of cached value.</typeparam>
    /// <param name="id">The unique identifier within this region.</param>
    /// <param name="factory">Factory function to create the value if not cached.</param>
    /// <param name="options">Cache entry options (TTL, stampede protection, etc.).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The cached or newly created value.</returns>
    ValueTask<T> GetOrCreateAsync<T>(
        string id,
        Func<CancellationToken, ValueTask<T>> factory,
        CacheEntryOptions options = default,
        CancellationToken ct = default);

    /// <summary>
    /// Gets a cached value without a factory fallback.
    /// Returns default(T) if the key doesn't exist.
    /// </summary>
    ValueTask<T?> GetAsync<T>(string id, CancellationToken ct = default);

    /// <summary>
    /// Sets a value in the cache with the specified options.
    /// </summary>
    ValueTask SetAsync<T>(string id, T value, CacheEntryOptions options = default, CancellationToken ct = default);

    /// <summary>
    /// Removes a cache entry from this region.
    /// </summary>
    /// <param name="id">The unique identifier within this region.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the entry was removed, false if it didn't exist.</returns>
    ValueTask<bool> RemoveAsync(string id, CancellationToken ct = default);
}
