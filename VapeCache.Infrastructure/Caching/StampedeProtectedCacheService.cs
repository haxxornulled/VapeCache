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
        public int Disposed; // 0 = active, 1 = disposed (prevents race on disposal)
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

    public ValueTask<T?> GetAsync<T>(string key, SpanDeserializer<T> deserialize, CancellationToken ct)
        => _inner.GetAsync(key, deserialize, ct);

    public ValueTask SetAsync<T>(string key, T value, Action<IBufferWriter<byte>, T> serialize, CacheEntryOptions options, CancellationToken ct)
        => _inner.SetAsync(key, value, serialize, options, ct);

    public async ValueTask<T> GetOrSetAsync<T>(
        string key,
        Func<CancellationToken, ValueTask<T>> factory,
        Action<IBufferWriter<byte>, T> serialize,
        SpanDeserializer<T> deserialize,
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

        LockEntry entry;
        while (true)
        {
            entry = _locks.GetOrAdd(key, static _ => new LockEntry());

            // CRITICAL: Check if this entry is already marked for disposal before incrementing refcount
            // This prevents the race where we get a stale entry that's being disposed
            if (Volatile.Read(ref entry.Disposed) == 1)
            {
                // Entry is being disposed, retry to get the new entry
                continue;
            }

            Interlocked.Increment(ref entry.RefCount);

            // Double-check after increment - if it was disposed between read and increment, decrement and retry
            if (Volatile.Read(ref entry.Disposed) == 1)
            {
                Interlocked.Decrement(ref entry.RefCount);
                continue;
            }

            // Safe to use this entry
            break;
        }

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
            // Decrement refcount and attempt cleanup
            var newCount = Interlocked.Decrement(ref entry.RefCount);
            if (newCount == 0)
            {
                // Mark as disposed to prevent new callers from using this entry
                if (Interlocked.CompareExchange(ref entry.Disposed, 1, 0) == 0)
                {
                    // Successfully marked as disposed, now safe to remove and dispose
                    // Even if TryRemove fails (rare race), the disposed flag prevents reuse
                    if (_locks.TryRemove(new KeyValuePair<string, LockEntry>(key, entry)))
                    {
                        entry.Semaphore.Dispose();
                    }
                }
            }
        }
    }
}
