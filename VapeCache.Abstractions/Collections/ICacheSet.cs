namespace VapeCache.Abstractions.Collections;

/// <summary>
/// Typed Redis SET operations with automatic serialization/deserialization.
/// Sets are unordered collections of unique items with fast membership testing.
/// </summary>
/// <typeparam name="T">The type of items stored in the set</typeparam>
public interface ICacheSet<T>
{
    /// <summary>Gets the Redis key for this set</summary>
    string Key { get; }

    /// <summary>Add an item to the set (idempotent)</summary>
    /// <returns>Number of items actually added (0 if already exists, 1 if new)</returns>
    ValueTask<long> AddAsync(T item, CancellationToken ct = default);

    /// <summary>Remove an item from the set</summary>
    /// <returns>Number of items actually removed (0 if not found, 1 if removed)</returns>
    ValueTask<long> RemoveAsync(T item, CancellationToken ct = default);

    /// <summary>Check if an item exists in the set</summary>
    ValueTask<bool> ContainsAsync(T item, CancellationToken ct = default);

    /// <summary>Get all members of the set</summary>
    ValueTask<T[]> MembersAsync(CancellationToken ct = default);

    /// <summary>Get the number of items in the set (cardinality)</summary>
    ValueTask<long> CountAsync(CancellationToken ct = default);

    /// <summary>
    /// Stream set members using SSCAN to handle large sets efficiently.
    /// </summary>
    IAsyncEnumerable<T> StreamAsync(string? pattern = null, int pageSize = 128, CancellationToken ct = default);
}
