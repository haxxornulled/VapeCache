namespace VapeCache.Abstractions.Collections;

/// <summary>
/// Typed Redis HASH operations with automatic serialization/deserialization.
/// Hashes are field-value maps, like a dictionary stored in a single Redis key.
/// </summary>
/// <typeparam name="T">The type of values stored in the hash fields</typeparam>
public interface ICacheHash<T>
{
    /// <summary>Gets the Redis key for this hash</summary>
    string Key { get; }

    /// <summary>Set a field in the hash</summary>
    /// <returns>1 if new field, 0 if existing field updated</returns>
    ValueTask<long> SetAsync(string field, T value, CancellationToken ct = default);

    /// <summary>Get a field from the hash</summary>
    ValueTask<T?> GetAsync(string field, CancellationToken ct = default);

    /// <summary>Get multiple fields from the hash</summary>
    ValueTask<T?[]> GetManyAsync(string[] fields, CancellationToken ct = default);

    /// <summary>
    /// Stream hash fields and values using HSCAN to handle large hashes efficiently.
    /// </summary>
    IAsyncEnumerable<(string Field, T Value)> StreamAsync(string? pattern = null, int pageSize = 128, CancellationToken ct = default);
}
