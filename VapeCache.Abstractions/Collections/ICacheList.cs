namespace VapeCache.Abstractions.Collections;

/// <summary>
/// Typed Redis LIST operations with automatic serialization/deserialization.
/// Lists are ordered collections that support push/pop operations from both ends.
/// </summary>
/// <typeparam name="T">The type of items stored in the list</typeparam>
public interface ICacheList<T>
{
    /// <summary>Gets the Redis key for this list</summary>
    string Key { get; }

    /// <summary>Push an item to the front (left) of the list</summary>
    ValueTask<long> PushFrontAsync(T item, CancellationToken ct = default);

    /// <summary>Push an item to the back (right) of the list</summary>
    ValueTask<long> PushBackAsync(T item, CancellationToken ct = default);

    /// <summary>Pop an item from the front (left) of the list</summary>
    ValueTask<T?> PopFrontAsync(CancellationToken ct = default);

    /// <summary>Pop an item from the back (right) of the list</summary>
    ValueTask<T?> PopBackAsync(CancellationToken ct = default);

    /// <summary>Get a range of items without removing them</summary>
    /// <param name="start">Zero-based start index (0 = first item, -1 = last item)</param>
    /// <param name="stop">Zero-based stop index (inclusive)</param>
    ValueTask<T[]> RangeAsync(long start, long stop, CancellationToken ct = default);

    /// <summary>Get the length of the list</summary>
    ValueTask<long> LengthAsync(CancellationToken ct = default);
}
