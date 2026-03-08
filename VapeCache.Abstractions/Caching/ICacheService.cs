using System.Buffers;

namespace VapeCache.Abstractions.Caching;

/// <summary>
/// Defines the cache service contract.
/// </summary>
public interface ICacheService
{
    /// <summary>
    /// Gets the name.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Executes get async.
    /// </summary>
    ValueTask<byte[]?> GetAsync(string key, CancellationToken ct);
    /// <summary>
    /// Executes set async.
    /// </summary>
    ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CacheEntryOptions options, CancellationToken ct);
    /// <summary>
    /// Executes remove async.
    /// </summary>
    ValueTask<bool> RemoveAsync(string key, CancellationToken ct);

    /// <summary>
    /// Provides member behavior.
    /// </summary>
    ValueTask<T?> GetAsync<T>(string key, SpanDeserializer<T> deserialize, CancellationToken ct);
    /// <summary>
    /// Provides member behavior.
    /// </summary>
    ValueTask SetAsync<T>(string key, T value, Action<IBufferWriter<byte>, T> serialize, CacheEntryOptions options, CancellationToken ct);

    /// <summary>
    /// Provides member behavior.
    /// </summary>
    ValueTask<T> GetOrSetAsync<T>(
        string key,
        Func<CancellationToken, ValueTask<T>> factory,
        Action<IBufferWriter<byte>, T> serialize,
        SpanDeserializer<T> deserialize,
        CacheEntryOptions options,
        CancellationToken ct);
}
