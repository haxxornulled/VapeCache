namespace VapeCache.Features.Search;

/// <summary>
/// Compiled RediSearch query with optional paging information.
/// </summary>
public sealed class RedisSearchQuery
{
    /// <summary>
    /// Creates a RediSearch query.
    /// </summary>
    public RedisSearchQuery(string rawQuery, int? offset = null, int? count = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawQuery);

        if (offset.HasValue && offset.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Offset cannot be negative.");

        if (count.HasValue && count.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(count), count, "Count must be greater than zero.");

        RawQuery = rawQuery;
        Offset = offset;
        Count = count;
    }

    /// <summary>
    /// Raw RediSearch query text.
    /// </summary>
    public string RawQuery { get; }

    /// <summary>
    /// Optional result offset.
    /// </summary>
    public int? Offset { get; }

    /// <summary>
    /// Optional result count.
    /// </summary>
    public int? Count { get; }
}
