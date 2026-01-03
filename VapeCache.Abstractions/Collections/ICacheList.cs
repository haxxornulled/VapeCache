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

    /// <summary>
    /// Try to pop an item from the front without waiting for enqueue capacity.
    /// Returns false when the operation cannot be enqueued immediately.
    /// </summary>
    bool TryPopFrontAsync(CancellationToken ct, out ValueTask<T?> task);

    /// <summary>
    /// Try to pop an item from the back without waiting for enqueue capacity.
    /// Returns false when the operation cannot be enqueued immediately.
    /// </summary>
    bool TryPopBackAsync(CancellationToken ct, out ValueTask<T?> task);

    /// <summary>Get a range of items without removing them</summary>
    /// <param name="start">Zero-based start index (0 = first item, -1 = last item)</param>
    /// <param name="stop">Zero-based stop index (inclusive)</param>
    ValueTask<T[]> RangeAsync(long start, long stop, CancellationToken ct = default);

    /// <summary>Get the length of the list</summary>
    ValueTask<long> LengthAsync(CancellationToken ct = default);

    /// <summary>
    /// Stream list items in pages to avoid loading large lists into memory.
    /// </summary>
    IAsyncEnumerable<T> StreamAsync(int pageSize = 128, CancellationToken ct = default);
}
