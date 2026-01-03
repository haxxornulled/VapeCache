namespace VapeCache.Abstractions.Collections;

/// <summary>
/// Typed Redis ZSET operations with automatic serialization/deserialization.
/// Sorted sets store unique members with an associated score.
/// </summary>
/// <typeparam name="T">The type of members stored in the sorted set</typeparam>
public interface ICacheSortedSet<T>
{
    /// <summary>Gets the Redis key for this sorted set</summary>
    string Key { get; }

    /// <summary>Add or update a member score</summary>
    /// <returns>1 if a new member was added, 0 if the score was updated</returns>
    ValueTask<long> AddAsync(T member, double score, CancellationToken ct = default);

    /// <summary>Remove a member from the sorted set</summary>
    ValueTask<long> RemoveAsync(T member, CancellationToken ct = default);

    /// <summary>Get the score for a member, or null if not present</summary>
    ValueTask<double?> ScoreAsync(T member, CancellationToken ct = default);

    /// <summary>Get the rank (0-based) for a member, or null if not present</summary>
    ValueTask<long?> RankAsync(T member, bool descending = false, CancellationToken ct = default);

    /// <summary>Increment a member score and return the updated score</summary>
    ValueTask<double> IncrementAsync(T member, double increment, CancellationToken ct = default);

    /// <summary>Get the number of members in the sorted set</summary>
    ValueTask<long> CountAsync(CancellationToken ct = default);

    /// <summary>Get a range of members by rank (inclusive)</summary>
    ValueTask<(T Member, double Score)[]> RangeByRankAsync(long start, long stop, bool descending = false, CancellationToken ct = default);

    /// <summary>Get a range of members by score (inclusive)</summary>
    ValueTask<(T Member, double Score)[]> RangeByScoreAsync(
        double min,
        double max,
        bool descending = false,
        long? offset = null,
        long? count = null,
        CancellationToken ct = default);

    /// <summary>Stream members using ZSCAN to handle large sorted sets efficiently</summary>
    IAsyncEnumerable<(T Member, double Score)> StreamAsync(string? pattern = null, int pageSize = 128, CancellationToken ct = default);
}
