using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;

namespace VapeCache.Infrastructure.Caching;

internal sealed class StampedeProtectedCacheService : ICacheService
{
    private sealed class FailureEntry
    {
        public long RetryAfterUtcTicks;
    }

    private sealed class LockEntry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int RefCount;
        public int Disposed; // 0 = active, 1 = disposed (prevents race on disposal)
    }

    private readonly ICacheService _inner;
    private readonly IOptionsMonitor<CacheStampedeOptions> _optionsMonitor;
    private readonly CacheStats? _stats;
    private CacheStampedeOptions _options => _optionsMonitor.CurrentValue;
    private readonly ConcurrentDictionary<string, LockEntry> _locks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FailureEntry> _failures = new(StringComparer.Ordinal);
    private int _lockKeyCount;

    public StampedeProtectedCacheService(ICacheService inner, IOptionsMonitor<CacheStampedeOptions> options, CacheStats? stats = null)
    {
        _inner = inner;
        _optionsMonitor = options;
        _stats = stats;
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

    public async ValueTask<T> GetOrSetAsync<T>(
        string key,
        Func<CancellationToken, ValueTask<T>> factory,
        Action<IBufferWriter<byte>, T> serialize,
        SpanDeserializer<T> deserialize,
        CacheEntryOptions options,
        CancellationToken ct)
    {
        var stampedeOptions = _options;
        if (!stampedeOptions.Enabled)
            return await _inner.GetOrSetAsync(key, factory, serialize, deserialize, options, ct).ConfigureAwait(false);

        ValidateKey(key, stampedeOptions);
        ThrowIfInFailureBackoff(key, stampedeOptions);

        var bytes = await _inner.GetAsync(key, ct).ConfigureAwait(false);
        if (bytes is not null)
        {
            _failures.TryRemove(key, out _);
            return deserialize(bytes);
        }

        if (Volatile.Read(ref _lockKeyCount) > stampedeOptions.MaxKeys)
        {
            // Fail open: don't deadlock the app due to lock map growth.
            return await _inner.GetOrSetAsync(key, factory, serialize, deserialize, options, ct).ConfigureAwait(false);
        }

        LockEntry entry;
        while (true)
        {
            if (!_locks.TryGetValue(key, out entry!))
            {
                var created = new LockEntry();
                if (!_locks.TryAdd(key, created))
                    continue;

                Interlocked.Increment(ref _lockKeyCount);
                entry = created;
            }

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
            var lockTaken = false;
            using var waitCts = CreateWaitTokenSource(stampedeOptions, ct);
            try
            {
                await entry.Semaphore.WaitAsync(waitCts?.Token ?? ct).ConfigureAwait(false);
                lockTaken = true;

                bytes = await _inner.GetAsync(key, ct).ConfigureAwait(false);
                if (bytes is not null)
                {
                    _failures.TryRemove(key, out _);
                    return deserialize(bytes);
                }

                var created = await factory(ct).ConfigureAwait(false);
                await _inner.SetAsync(key, created, serialize, options, ct).ConfigureAwait(false);
                _failures.TryRemove(key, out _);
                return created;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                CacheTelemetry.StampedeLockWaitTimeout.Add(1);
                _stats?.IncStampedeLockWaitTimeout();
                throw new TimeoutException($"Stampede lock wait timed out for key '{key}'.");
            }
            catch
            {
                if (lockTaken)
                    RegisterFailure(key, stampedeOptions);
                throw;
            }
            finally
            {
                if (lockTaken)
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
                        Interlocked.Decrement(ref _lockKeyCount);
                        entry.Semaphore.Dispose();
                    }
                }
            }
        }
    }

    private void ValidateKey(string key, CacheStampedeOptions options)
    {
        if (!options.RejectSuspiciousKeys)
            return;

        if (string.IsNullOrWhiteSpace(key))
        {
            CacheTelemetry.StampedeKeyRejected.Add(1, new TagList { { "reason", "empty" } });
            _stats?.IncStampedeKeyRejected();
            throw new ArgumentException("Cache key must not be null or empty.", nameof(key));
        }

        if (key.Length > options.MaxKeyLength)
        {
            CacheTelemetry.StampedeKeyRejected.Add(1, new TagList { { "reason", "max_length" } });
            _stats?.IncStampedeKeyRejected();
            throw new ArgumentException($"Cache key length ({key.Length}) exceeds configured max ({options.MaxKeyLength}).", nameof(key));
        }

        for (var i = 0; i < key.Length; i++)
        {
            if (char.IsControl(key[i]))
            {
                CacheTelemetry.StampedeKeyRejected.Add(1, new TagList { { "reason", "control_char" } });
                _stats?.IncStampedeKeyRejected();
                throw new ArgumentException("Cache key contains control characters.", nameof(key));
            }
        }
    }

    private static CancellationTokenSource? CreateWaitTokenSource(CacheStampedeOptions options, CancellationToken ct)
    {
        if (options.LockWaitTimeout <= TimeSpan.Zero)
            return null;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(options.LockWaitTimeout);
        return cts;
    }

    private void ThrowIfInFailureBackoff(string key, CacheStampedeOptions options)
    {
        if (!options.EnableFailureBackoff || options.FailureBackoff <= TimeSpan.Zero)
            return;

        if (!_failures.TryGetValue(key, out var state))
            return;

        if (DateTime.UtcNow.Ticks < Volatile.Read(ref state.RetryAfterUtcTicks))
        {
            CacheTelemetry.StampedeFailureBackoffRejected.Add(1);
            _stats?.IncStampedeFailureBackoffRejected();
            throw new InvalidOperationException($"Factory for key '{key}' is in failure backoff window.");
        }

        _failures.TryRemove(key, out _);
    }

    private void RegisterFailure(string key, CacheStampedeOptions options)
    {
        if (!options.EnableFailureBackoff || options.FailureBackoff <= TimeSpan.Zero)
            return;

        var retryAfterTicks = DateTime.UtcNow.Add(options.FailureBackoff).Ticks;
        var entry = _failures.GetOrAdd(key, static _ => new FailureEntry());
        Volatile.Write(ref entry.RetryAfterUtcTicks, retryAfterTicks);
    }
}
