using System.Buffers;
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;

namespace VapeCache.Infrastructure.Caching;

internal sealed class StampedeProtectedCacheService : ICacheService
{
    private sealed class LockEntry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int RefCount;
    }

    private readonly ICacheService _inner;
    private readonly CacheStampedeOptions _options;
    private readonly ConcurrentDictionary<string, LockEntry> _locks = new(StringComparer.Ordinal);

    public StampedeProtectedCacheService(ICacheService inner, IOptions<CacheStampedeOptions> options)
    {
        _inner = inner;
        _options = options.Value;
    }

    public string Name => _inner.Name;

    public ValueTask<byte[]?> GetAsync(string key, CancellationToken ct) => _inner.GetAsync(key, ct);

    public ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CacheEntryOptions options, CancellationToken ct)
        => _inner.SetAsync(key, value, options, ct);

    public ValueTask<bool> RemoveAsync(string key, CancellationToken ct) => _inner.RemoveAsync(key, ct);

    public ValueTask<T?> GetAsync<T>(string key, Func<ReadOnlySpan<byte>, T> deserialize, CancellationToken ct)
        => _inner.GetAsync(key, deserialize, ct);

    public ValueTask SetAsync<T>(string key, T value, Action<IBufferWriter<byte>, T> serialize, CacheEntryOptions options, CancellationToken ct)
        => _inner.SetAsync(key, value, serialize, options, ct);

    public async ValueTask<T> GetOrSetAsync<T>(
        string key,
        Func<CancellationToken, ValueTask<T>> factory,
        Action<IBufferWriter<byte>, T> serialize,
        Func<ReadOnlySpan<byte>, T> deserialize,
        CacheEntryOptions options,
        CancellationToken ct)
    {
        if (!_options.Enabled)
            return await _inner.GetOrSetAsync(key, factory, serialize, deserialize, options, ct).ConfigureAwait(false);

        var bytes = await _inner.GetAsync(key, ct).ConfigureAwait(false);
        if (bytes is not null)
            return deserialize(bytes);

        if (_locks.Count > _options.MaxKeys)
        {
            // Fail open: don't deadlock the app due to lock map growth.
            return await _inner.GetOrSetAsync(key, factory, serialize, deserialize, options, ct).ConfigureAwait(false);
        }

        var entry = _locks.GetOrAdd(key, static _ => new LockEntry());
        Interlocked.Increment(ref entry.RefCount);

        try
        {
            await entry.Semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                bytes = await _inner.GetAsync(key, ct).ConfigureAwait(false);
                if (bytes is not null)
                    return deserialize(bytes);

                var created = await factory(ct).ConfigureAwait(false);
                await _inner.SetAsync(key, created, serialize, options, ct).ConfigureAwait(false);
                return created;
            }
            finally
            {
                entry.Semaphore.Release();
            }
        }
        finally
        {
            if (Interlocked.Decrement(ref entry.RefCount) == 0 && _locks.TryRemove(new KeyValuePair<string, LockEntry>(key, entry)))
                entry.Semaphore.Dispose();
        }
    }
}
