using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Core.Policies;

namespace VapeCache.Infrastructure.Caching;

internal sealed class StampedeProtectedCacheService : ICacheService, ICacheTagService
{
    private sealed class FailureEntry
    {
        public long RetryAfterUtcTicks;
    }

    private sealed class LockEntry : IDisposable
    {
        private readonly AsyncSignal _completion = new();
        private int _inFlight;
        public int RefCount;
        public int Disposed; // 0 = active, 1 = disposed (prevents race on disposal)

        public long CompletionVersion => _completion.Version;

        public bool TryBeginSingleFlight()
            => Interlocked.CompareExchange(ref _inFlight, 1, 0) == 0;

        public bool IsInFlight => Volatile.Read(ref _inFlight) == 1;

        public ValueTask WaitForCompletionAsync(long observedVersion, CancellationToken ct)
            => _completion.WaitAsync(observedVersion, ct);

        public void CompleteSingleFlight()
        {
            Volatile.Write(ref _inFlight, 0);
            _completion.Set();
        }

        public void Dispose() => _completion.Dispose();
    }

    private readonly ICacheService _inner;
    private readonly IOptionsMonitor<CacheStampedeOptions> _optionsMonitor;
    private readonly CacheStats? _stats;
    private CacheStampedeOptions _options => _optionsMonitor.CurrentValue;
    private readonly ConcurrentDictionary<string, LockEntry> _locks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FailureEntry> _failures = new(StringComparer.Ordinal);
    private int _lockKeyCount;

    [ActivatorUtilitiesConstructor]
    public StampedeProtectedCacheService(
        HybridCacheService inner,
        IOptionsMonitor<CacheStampedeOptions> options,
        CacheStatsRegistry statsRegistry)
        : this(
            (ICacheService)inner,
            options,
            statsRegistry.GetOrCreate(CacheStatsNames.Hybrid))
    {
    }

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

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask<long> InvalidateTagAsync(string tag, CancellationToken ct = default)
        => RequireTagService().InvalidateTagAsync(tag, ct);

    /// <summary>
    /// Gets value.
    /// </summary>
    public ValueTask<long> GetTagVersionAsync(string tag, CancellationToken ct = default)
        => RequireTagService().GetTagVersionAsync(tag, ct);

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask<long> InvalidateZoneAsync(string zone, CancellationToken ct = default)
        => RequireTagService().InvalidateZoneAsync(zone, ct);

    /// <summary>
    /// Gets value.
    /// </summary>
    public ValueTask<long> GetZoneVersionAsync(string zone, CancellationToken ct = default)
        => RequireTagService().GetZoneVersionAsync(zone, ct);

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

        ct.ThrowIfCancellationRequested();
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
            using var waitCts = CreateWaitTokenSource(stampedeOptions, ct);
            while (true)
            {
                if (entry.TryBeginSingleFlight())
                {
                    try
                    {
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
                    catch
                    {
                        RegisterFailure(key, stampedeOptions);
                        throw;
                    }
                    finally
                    {
                        entry.CompleteSingleFlight();
                    }
                }

                var observedVersion = entry.CompletionVersion;
                if (!entry.IsInFlight)
                    continue;

                try
                {
                    await entry.WaitForCompletionAsync(observedVersion, waitCts?.Token ?? ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    CacheTelemetry.StampedeLockWaitTimeout.Add(1);
                    _stats?.IncStampedeLockWaitTimeout();
                    throw new TimeoutException($"Stampede lock wait timed out for key '{key}'.");
                }

                ThrowIfInFailureBackoff(key, stampedeOptions);

                bytes = await _inner.GetAsync(key, ct).ConfigureAwait(false);
                if (bytes is not null)
                {
                    _failures.TryRemove(key, out _);
                    return deserialize(bytes);
                }
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
                        entry.Dispose();
                    }
                }
            }
        }
    }

    private void ValidateKey(string key, CacheStampedeOptions options)
    {
        var decision = StampedeRuntimePolicy.ValidateKey(key, options.RejectSuspiciousKeys, options.MaxKeyLength);
        if (decision.IsValid)
            return;

        CacheTelemetry.StampedeKeyRejected.Add(1, new TagList { { "reason", MapKeyRejectionTag(decision.Reason) } });
        _stats?.IncStampedeKeyRejected();
        throw CreateKeyValidationException(key, options, decision.Reason);
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
        if (!StampedeRuntimePolicy.IsFailureBackoffConfigured(options.EnableFailureBackoff, options.FailureBackoff))
            return;

        if (!_failures.TryGetValue(key, out var state))
            return;

        if (StampedeRuntimePolicy.IsWithinFailureBackoffWindow(DateTime.UtcNow.Ticks, Volatile.Read(ref state.RetryAfterUtcTicks)))
        {
            CacheTelemetry.StampedeFailureBackoffRejected.Add(1);
            _stats?.IncStampedeFailureBackoffRejected();
            throw new InvalidOperationException($"Factory for key '{key}' is in failure backoff window.");
        }

        _failures.TryRemove(key, out _);
    }

    private void RegisterFailure(string key, CacheStampedeOptions options)
    {
        if (!StampedeRuntimePolicy.IsFailureBackoffConfigured(options.EnableFailureBackoff, options.FailureBackoff))
            return;

        var retryAfterTicks = StampedeRuntimePolicy.ComputeRetryAfterUtcTicks(DateTime.UtcNow.Ticks, options.FailureBackoff);
        var entry = _failures.GetOrAdd(key, static _ => new FailureEntry());
        Volatile.Write(ref entry.RetryAfterUtcTicks, retryAfterTicks);
    }

    private static string MapKeyRejectionTag(StampedeKeyRejectionReason reason) =>
        reason switch
        {
            StampedeKeyRejectionReason.Empty => "empty",
            StampedeKeyRejectionReason.MaxLength => "max_length",
            StampedeKeyRejectionReason.ControlCharacter => "control_char",
            _ => "unknown"
        };

    private static ArgumentException CreateKeyValidationException(string key, CacheStampedeOptions options, StampedeKeyRejectionReason reason) =>
        reason switch
        {
            StampedeKeyRejectionReason.Empty =>
                new ArgumentException("Cache key must not be null or empty.", nameof(key)),
            StampedeKeyRejectionReason.MaxLength =>
                new ArgumentException($"Cache key length ({key.Length}) exceeds configured max ({options.MaxKeyLength}).", nameof(key)),
            StampedeKeyRejectionReason.ControlCharacter =>
                new ArgumentException("Cache key contains control characters.", nameof(key)),
            _ =>
                new ArgumentException("Cache key failed stampede validation.", nameof(key))
        };

    private ICacheTagService RequireTagService()
    {
        if (_inner is ICacheTagService tags)
            return tags;

        throw new NotSupportedException(
            $"Inner cache service '{_inner.GetType().Name}' does not implement tag/zone invalidation.");
    }

    private sealed class AsyncSignal : IDisposable
    {
        private volatile TaskCompletionSource<bool>? _waiters;
        private long _version;
        private int _disposed;

        public long Version => Volatile.Read(ref _version);

        public void Set()
        {
            if (Volatile.Read(ref _disposed) == 1)
                return;

            Interlocked.Increment(ref _version);
            Interlocked.Exchange(ref _waiters, null)?.TrySetResult(true);
        }

        public ValueTask WaitAsync(long observedVersion, CancellationToken ct)
        {
            if (Volatile.Read(ref _disposed) == 1 || Volatile.Read(ref _version) != observedVersion)
                return ValueTask.CompletedTask;

            while (true)
            {
                var current = _waiters;
                if (Volatile.Read(ref _disposed) == 1 || Volatile.Read(ref _version) != observedVersion)
                    return ValueTask.CompletedTask;

                if (current is null)
                {
                    var created = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var prior = Interlocked.CompareExchange(ref _waiters, created, null);
                    current = prior ?? created;
                    if (prior is not null)
                        continue;
                }

                if (Volatile.Read(ref _disposed) == 1 || Volatile.Read(ref _version) != observedVersion)
                    return ValueTask.CompletedTask;

                return new ValueTask(current.Task.WaitAsync(ct));
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;

            Interlocked.Increment(ref _version);
            Interlocked.Exchange(ref _waiters, null)?.TrySetResult(true);
        }
    }
}
