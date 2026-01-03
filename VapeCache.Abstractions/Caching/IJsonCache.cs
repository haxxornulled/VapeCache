using VapeCache.Abstractions.Connections;

namespace VapeCache.Abstractions.Caching;

/// <summary>
/// JSON cache facade that uses RedisJSON when available and falls back to plain GET/SET.
/// </summary>
public interface IJsonCache
{
    /// <summary>Returns true when RedisJSON is available.</summary>
    ValueTask<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>Get a JSON value, optionally at a JSONPath.</summary>
    ValueTask<T?> GetAsync<T>(string key, string? path = null, CancellationToken ct = default);

    /// <summary>Get a JSON value as a lease to avoid extra byte[] allocations.</summary>
    ValueTask<RedisValueLease> GetLeaseAsync(string key, string? path = null, CancellationToken ct = default);

    /// <summary>Set a JSON value, optionally at a JSONPath.</summary>
    ValueTask SetAsync<T>(string key, T value, string? path = null, CancellationToken ct = default);

    /// <summary>Set a JSON value using a lease payload.</summary>
    ValueTask SetLeaseAsync(string key, RedisValueLease json, string? path = null, CancellationToken ct = default);

    /// <summary>Delete a JSON value or path.</summary>
    ValueTask<long> DeleteAsync(string key, string? path = null, CancellationToken ct = default);
}
