using System.Buffers;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;

namespace VapeCache.Infrastructure.Caching;

internal sealed class HybridStampedeCacheService : ICacheService
{
    private readonly StampedeProtectedCacheService _inner;

    public HybridStampedeCacheService(HybridCacheService hybrid, IOptionsMonitor<CacheStampedeOptions> options, CacheStatsRegistry statsRegistry)
    {
        _inner = new StampedeProtectedCacheService(hybrid, options, statsRegistry.GetOrCreate(CacheStatsNames.Hybrid));
    }

    public string Name => _inner.Name;

    /// <summary>
    /// Gets value.
    /// </summary>
    public ValueTask<byte[]?> GetAsync(string key, CancellationToken ct) => _inner.GetAsync(key, ct);

    /// <summary>
    /// Sets value.
    /// </summary>
    public ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CacheEntryOptions options, CancellationToken ct)
        => _inner.SetAsync(key, value, options, ct);

    /// <summary>
    /// Removes value.
    /// </summary>
    public ValueTask<bool> RemoveAsync(string key, CancellationToken ct) => _inner.RemoveAsync(key, ct);

    public ValueTask<T?> GetAsync<T>(string key, SpanDeserializer<T> deserialize, CancellationToken ct)
        => _inner.GetAsync(key, deserialize, ct);

    public ValueTask SetAsync<T>(string key, T value, Action<IBufferWriter<byte>, T> serialize, CacheEntryOptions options, CancellationToken ct)
        => _inner.SetAsync(key, value, serialize, options, ct);

    public ValueTask<T> GetOrSetAsync<T>(
        string key,
        Func<CancellationToken, ValueTask<T>> factory,
        Action<IBufferWriter<byte>, T> serialize,
        SpanDeserializer<T> deserialize,
        CacheEntryOptions options,
        CancellationToken ct)
        => _inner.GetOrSetAsync(key, factory, serialize, deserialize, options, ct);
}
