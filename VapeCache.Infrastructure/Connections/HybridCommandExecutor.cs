using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Caching;

namespace VapeCache.Infrastructure.Connections;

/// <summary>
/// Hybrid Redis command executor that automatically falls back to a configured executor
/// when Redis is unavailable. Applies circuit breaker pattern to all Redis data structure operations.
/// </summary>
internal sealed partial class HybridCommandExecutor : IRedisCommandExecutor, IRedisMultiplexerDiagnostics
{
    private readonly RedisCommandExecutor _redis;
    private readonly IRedisFallbackCommandExecutor _fallback;
    private readonly IRedisCircuitBreakerState _breakerState;
    private readonly IRedisFailoverController _breakerController;
    private readonly CacheStats _stats;
    private readonly ICurrentCacheService _current;
    private readonly ILogger<HybridCommandExecutor> _logger;
    private readonly IOptionsMonitor<RedisCircuitBreakerOptions> _breakerOptions;
    private RedisCircuitBreakerOptions _breaker => _breakerOptions.CurrentValue;
    private int _halfOpenProbes;

    public HybridCommandExecutor(
        RedisCommandExecutor redis,
        IRedisFallbackCommandExecutor fallback,
        IRedisCircuitBreakerState breakerState,
        IRedisFailoverController breakerController,
        CacheStatsRegistry statsRegistry,
        ICurrentCacheService current,
        IOptionsMonitor<RedisCircuitBreakerOptions> breakerOptions,
        ILogger<HybridCommandExecutor> logger)
    {
        _redis = redis;
        _fallback = fallback;
        _breakerState = breakerState;
        _breakerController = breakerController;
        _stats = statsRegistry.GetOrCreate(CacheStatsNames.Hybrid);
        _current = current;
        _logger = logger;
        _breakerOptions = breakerOptions;
    }

    /// <summary>
    /// Creates value.
    /// </summary>
    public IRedisBatch CreateBatch()
        => new RedisBatch(this);

    /// <summary>
    /// Gets value.
    /// </summary>
    public RedisAutoscalerSnapshot GetAutoscalerSnapshot()
        => _redis.GetAutoscalerSnapshot();

    /// <summary>
    /// Gets value.
    /// </summary>
    public IReadOnlyList<RedisMuxLaneSnapshot> GetMuxLaneSnapshots()
        => _redis.GetMuxLaneSnapshots();

    private readonly struct ProbeScope : IDisposable
    {
        private readonly HybridCommandExecutor _owner;
        private readonly CancellationTokenSource? _cts;
        private readonly bool _tookSlot;

        public bool Allowed { get; }
        public bool Throttled { get; }
        public CancellationToken Token { get; }

        public ProbeScope(bool allowed, bool throttled, bool tookSlot, CancellationToken token, CancellationTokenSource? cts, HybridCommandExecutor owner)
        {
            Allowed = allowed;
            Throttled = throttled;
            _tookSlot = tookSlot;
            Token = token;
            _cts = cts;
            _owner = owner;
        }

        /// <summary>
        /// Releases resources used by the current instance.
        /// </summary>
        public void Dispose()
        {
            _cts?.Dispose();
            if (_tookSlot)
                Interlocked.Decrement(ref _owner._halfOpenProbes);
        }
    }

    private ProbeScope StartProbe(CancellationToken ct)
    {
        if (!_breaker.Enabled)
            return new ProbeScope(allowed: true, throttled: false, tookSlot: false, token: ct, cts: null, owner: this);

        // Only throttle when we've opened previously (consecutive failures at/above threshold) or when half-open probes are in flight.
        var useSlot = _breakerState.ConsecutiveFailures >= _breaker.ConsecutiveFailuresToOpen || _breakerState.HalfOpenProbeInFlight;
        if (!useSlot)
            return new ProbeScope(allowed: true, throttled: false, tookSlot: false, token: ct, cts: null, owner: this);

        var probeNum = Interlocked.Increment(ref _halfOpenProbes);
        if (probeNum > _breaker.MaxHalfOpenProbes)
        {
            Interlocked.Decrement(ref _halfOpenProbes);
            return new ProbeScope(allowed: false, throttled: true, tookSlot: false, token: ct, cts: null, owner: this);
        }

        CancellationTokenSource? cts = null;
        if (_breaker.HalfOpenProbeTimeout > TimeSpan.Zero)
        {
            cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_breaker.HalfOpenProbeTimeout);
        }

        return new ProbeScope(allowed: true, throttled: false, tookSlot: true, token: cts?.Token ?? ct, cts: cts, owner: this);
    }

    // Retry helper with exponential backoff for transient failures
    private async ValueTask<T> ExecuteWithRetryAsync<T>(
        Func<ValueTask<T>> operation,
        string operationName,
        CancellationToken ct,
        int maxRetries = 2)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < maxRetries && IsTransientError(ex))
            {
                attempt++;
                var delay = TimeSpan.FromMilliseconds(Math.Pow(2, attempt) * 10); // 20ms, 40ms, 80ms
                LogTransientRetry(_logger, ex, operationName, attempt, maxRetries, delay.TotalMilliseconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransientError(Exception ex)
    {
        // Identify transient errors that are worth retrying
        var message = ex.Message;
        return message.Contains("Ring dequeue failed", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("temporarily unavailable", StringComparison.OrdinalIgnoreCase) ||
               ex is TimeoutException ||
               ex is OperationCanceledException && !message.Contains("user", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRedisIndexAlreadyExists(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException!)
        {
            if (current.Message.Contains("index already exists", StringComparison.OrdinalIgnoreCase))
                return true;

            if (current.InnerException is null)
                break;
        }

        return false;
    }

    private ValueTask<T> FailOpenTryAsync<T>(
        ValueTask<T> primary,
        Func<ValueTask<T>> fallback,
        CancellationToken ct)
    {
        if (primary.IsCompletedSuccessfully)
        {
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return primary;
        }

        return AwaitFailOpenTryAsync(primary, fallback, ct);

        async ValueTask<T> AwaitFailOpenTryAsync(ValueTask<T> task, Func<ValueTask<T>> fallbackInner, CancellationToken token)
        {
            try
            {
                var result = await task.ConfigureAwait(false);
                _breakerController.MarkRedisSuccess();
                _current.SetCurrent("redis");
                return result;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                _breakerController.MarkRedisFailure();
                _stats.IncFallbackToMemory();
                _current.SetCurrent(_fallback.Name);
                return await fallbackInner().ConfigureAwait(false);
            }
        }
    }

    private async IAsyncEnumerable<T> StreamWithFallback<T>(
        string op,
        Func<IAsyncEnumerable<T>> primary,
        Func<IAsyncEnumerable<T>> fallback,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            await foreach (var item in fallback().WithCancellation(ct).ConfigureAwait(false))
                yield return item;
            yield break;
        }

        Exception? error = null;
        await using (var enumerator = primary().GetAsyncEnumerator(ct))
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    error = ex;
                    break;
                }

                if (!moved)
                    break;

                yield return enumerator.Current;
            }
        }

        if (error is null)
        {
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            yield break;
        }

        _breakerController.MarkRedisFailure();
        _stats.IncFallbackToMemory();
        _current.SetCurrent(_fallback.Name);
        LogRedisOperationFallback(_logger, error, op);

        await foreach (var item in fallback().WithCancellation(ct).ConfigureAwait(false))
            yield return item;
    }

    // ========== Simple Key-Value Operations ==========

    /// <summary>
    /// Gets value.
    /// </summary>
    public async ValueTask<byte[]?> GetAsync(string key, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.GetAsync(key, ct).ConfigureAwait(false);
        }

        using var probe = StartProbe(ct);
        if (probe.Throttled)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.GetAsync(key, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.GetAsync(key, probe.Token).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "GET", key);
            return await _fallback.GetAsync(key, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets value.
    /// </summary>
    public async ValueTask<byte[]?> GetExAsync(string key, TimeSpan? ttl, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.GetExAsync(key, ttl, ct).ConfigureAwait(false);
        }

        using var probe = StartProbe(ct);
        if (probe.Throttled)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.GetExAsync(key, ttl, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.GetExAsync(key, ttl, probe.Token).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "GETEX", key);
            return await _fallback.GetExAsync(key, ttl, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<byte[]?[]> MGetAsync(string[] keys, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.MGetAsync(keys, ct).ConfigureAwait(false);
        }

        using var probe = StartProbe(ct);
        if (probe.Throttled)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.MGetAsync(keys, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.MGetAsync(keys, probe.Token).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisOperationFallback(_logger, ex, "MGET");
            return await _fallback.MGetAsync(keys, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sets value.
    /// </summary>
    public async ValueTask<bool> SetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan? ttl, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.SetAsync(key, value, ttl, ct).ConfigureAwait(false);
        }

        using var probe = StartProbe(ct);
        if (probe.Throttled)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.SetAsync(key, value, ttl, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.SetAsync(key, value, ttl, probe.Token).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "SET", key);
            return await _fallback.SetAsync(key, value, ttl, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<bool> MSetAsync((string Key, ReadOnlyMemory<byte> Value)[] items, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.MSetAsync(items, ct).ConfigureAwait(false);
        }

        using var probe = StartProbe(ct);
        if (probe.Throttled)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.MSetAsync(items, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.MSetAsync(items, probe.Token).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisOperationFallback(_logger, ex, "MSET");
            return await _fallback.MSetAsync(items, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<bool> DeleteAsync(string key, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.DeleteAsync(key, ct).ConfigureAwait(false);
        }

        using var probe = StartProbe(ct);
        if (probe.Throttled)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.DeleteAsync(key, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.DeleteAsync(key, probe.Token).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "DEL", key);
            return await _fallback.DeleteAsync(key, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long> TtlSecondsAsync(string key, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.TtlSecondsAsync(key, ct).ConfigureAwait(false);
        }

        using var probe = StartProbe(ct);
        if (probe.Throttled)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.TtlSecondsAsync(key, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.TtlSecondsAsync(key, probe.Token).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "TTL", key);
            return await _fallback.TtlSecondsAsync(key, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long> PTtlMillisecondsAsync(string key, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.PTtlMillisecondsAsync(key, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.PTtlMillisecondsAsync(key, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "PTTL", key);
            return await _fallback.PTtlMillisecondsAsync(key, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long> UnlinkAsync(string key, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.UnlinkAsync(key, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.UnlinkAsync(key, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "UNLINK", key);
            return await _fallback.UnlinkAsync(key, ct).ConfigureAwait(false);
        }
    }

    // ========== Lease-Based Reads ==========

    /// <summary>
    /// Gets value.
    /// </summary>
    public async ValueTask<RedisValueLease> GetLeaseAsync(string key, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.GetLeaseAsync(key, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.GetLeaseAsync(key, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "GET (lease)", key);
            return await _fallback.GetLeaseAsync(key, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public bool TryGetLeaseAsync(string key, CancellationToken ct, out ValueTask<RedisValueLease> task)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            task = _fallback.GetLeaseAsync(key, ct);
            return true;
        }

        if (_redis.TryGetLeaseAsync(key, ct, out var redisTask))
        {
            task = FailOpenTryAsync(redisTask, () => _fallback.GetLeaseAsync(key, ct), ct);
            return true;
        }

        _stats.IncFallbackToMemory();
        _current.SetCurrent(_fallback.Name);
        task = _fallback.GetLeaseAsync(key, ct);
        return true;
    }

    /// <summary>
    /// Gets value.
    /// </summary>
    public async ValueTask<RedisValueLease> GetExLeaseAsync(string key, TimeSpan? ttl, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.GetExLeaseAsync(key, ttl, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.GetExLeaseAsync(key, ttl, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "GETEX (lease)", key);
            return await _fallback.GetExLeaseAsync(key, ttl, ct).ConfigureAwait(false);
        }
    }

    // ========== Hash Operations ==========

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long> HSetAsync(string key, string field, ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.HSetAsync(key, field, value, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.HSetAsync(key, field, value, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFieldFallback(_logger, ex, "HSET", key, field);
            return await _fallback.HSetAsync(key, field, value, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<byte[]?> HGetAsync(string key, string field, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.HGetAsync(key, field, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.HGetAsync(key, field, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFieldFallback(_logger, ex, "HGET", key, field);
            return await _fallback.HGetAsync(key, field, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<byte[]?[]> HMGetAsync(string key, string[] fields, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.HMGetAsync(key, fields, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.HMGetAsync(key, fields, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "HMGET", key);
            return await _fallback.HMGetAsync(key, fields, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<RedisValueLease> HGetLeaseAsync(string key, string field, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.HGetLeaseAsync(key, field, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.HGetLeaseAsync(key, field, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFieldFallback(_logger, ex, "HGET (lease)", key, field);
            return await _fallback.HGetLeaseAsync(key, field, ct).ConfigureAwait(false);
        }
    }

    // ========== List Operations ==========

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long> LPushAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogBreakerOpenUsingFallback(_logger, "LPUSH", key);
            return await _fallback.LPushAsync(key, value, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.LPushAsync(key, value, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "LPUSH", key);
            return await _fallback.LPushAsync(key, value, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long> RPushAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.RPushAsync(key, value, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.RPushAsync(key, value, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "RPUSH", key);
            return await _fallback.RPushAsync(key, value, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long> RPushManyAsync(string key, ReadOnlyMemory<byte>[] values, int count, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.RPushManyAsync(key, values, count, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.RPushManyAsync(key, values, count, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "RPUSH (many)", key);
            return await _fallback.RPushManyAsync(key, values, count, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<byte[]?> LPopAsync(string key, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.LPopAsync(key, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.LPopAsync(key, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "LPOP", key);
            return await _fallback.LPopAsync(key, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public bool TryHGetAsync(string key, string field, CancellationToken ct, out ValueTask<byte[]?> task)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            task = _fallback.HGetAsync(key, field, ct);
            return true;
        }

        if (_redis.TryHGetAsync(key, field, ct, out var redisTask))
        {
            task = FailOpenTryAsync(redisTask, () => _fallback.HGetAsync(key, field, ct), ct);
            return true;
        }

        _stats.IncFallbackToMemory();
        _current.SetCurrent(_fallback.Name);
        task = _fallback.HGetAsync(key, field, ct);
        return true;
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public bool TryGetExLeaseAsync(string key, TimeSpan? ttl, CancellationToken ct, out ValueTask<RedisValueLease> task)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            task = _fallback.GetExLeaseAsync(key, ttl, ct);
            return true;
        }

        if (_redis.TryGetExLeaseAsync(key, ttl, ct, out var redisTask))
        {
            task = FailOpenTryAsync(redisTask, () => _fallback.GetExLeaseAsync(key, ttl, ct), ct);
            return true;
        }

        _stats.IncFallbackToMemory();
        _current.SetCurrent(_fallback.Name);
        task = _fallback.GetExLeaseAsync(key, ttl, ct);
        return true;
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public bool TrySetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan? ttl, CancellationToken ct, out ValueTask<bool> task)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            task = _fallback.SetAsync(key, value, ttl, ct);
            return true;
        }

        if (_redis.TrySetAsync(key, value, ttl, ct, out var redisTask))
        {
            task = FailOpenTryAsync(redisTask, () => _fallback.SetAsync(key, value, ttl, ct), ct);
            return true;
        }

        _stats.IncFallbackToMemory();
        _current.SetCurrent(_fallback.Name);
        task = _fallback.SetAsync(key, value, ttl, ct);
        return true;
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public bool TryGetExAsync(string key, TimeSpan? ttl, CancellationToken ct, out ValueTask<byte[]?> task)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            task = _fallback.GetExAsync(key, ttl, ct);
            return true;
        }

        if (_redis.TryGetExAsync(key, ttl, ct, out var redisTask))
        {
            task = FailOpenTryAsync(redisTask, () => _fallback.GetExAsync(key, ttl, ct), ct);
            return true;
        }

        _stats.IncFallbackToMemory();
        _current.SetCurrent(_fallback.Name);
        task = _fallback.GetExAsync(key, ttl, ct);
        return true;
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public bool TryGetAsync(string key, CancellationToken ct, out ValueTask<byte[]?> task)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            task = _fallback.GetAsync(key, ct);
            return true;
        }

        if (_redis.TryGetAsync(key, ct, out var redisTask))
        {
            task = FailOpenTryAsync(redisTask, () => _fallback.GetAsync(key, ct), ct);
            return true;
        }

        _stats.IncFallbackToMemory();
        _current.SetCurrent(_fallback.Name);
        task = _fallback.GetAsync(key, ct);
        return true;
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public bool TryLPopAsync(string key, CancellationToken ct, out ValueTask<byte[]?> task)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            task = _fallback.LPopAsync(key, ct);
            return true;
        }

        if (_redis.TryLPopAsync(key, ct, out var redisTask))
        {
            task = FailOpenTryAsync(redisTask, () => _fallback.LPopAsync(key, ct), ct);
            return true;
        }

        _stats.IncFallbackToMemory();
        _current.SetCurrent(_fallback.Name);
        task = _fallback.LPopAsync(key, ct);
        return true;
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<byte[]?> RPopAsync(string key, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.RPopAsync(key, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.RPopAsync(key, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "RPOP", key);
            return await _fallback.RPopAsync(key, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public bool TryRPopAsync(string key, CancellationToken ct, out ValueTask<byte[]?> task)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            task = _fallback.RPopAsync(key, ct);
            return true;
        }

        if (_redis.TryRPopAsync(key, ct, out var redisTask))
        {
            task = FailOpenTryAsync(redisTask, () => _fallback.RPopAsync(key, ct), ct);
            return true;
        }

        _stats.IncFallbackToMemory();
        _current.SetCurrent(_fallback.Name);
        task = _fallback.RPopAsync(key, ct);
        return true;
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<byte[]?[]> LRangeAsync(string key, long start, long stop, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.LRangeAsync(key, start, stop, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.LRangeAsync(key, start, stop, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "LRANGE", key);
            return await _fallback.LRangeAsync(key, start, stop, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long> LLenAsync(string key, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.LLenAsync(key, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.LLenAsync(key, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "LLEN", key);
            return await _fallback.LLenAsync(key, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<RedisValueLease> LPopLeaseAsync(string key, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.LPopLeaseAsync(key, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.LPopLeaseAsync(key, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "LPOP (lease)", key);
            return await _fallback.LPopLeaseAsync(key, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public bool TryLPopLeaseAsync(string key, CancellationToken ct, out ValueTask<RedisValueLease> task)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            task = _fallback.LPopLeaseAsync(key, ct);
            return true;
        }

        if (_redis.TryLPopLeaseAsync(key, ct, out var redisTask))
        {
            task = FailOpenTryAsync(redisTask, () => _fallback.LPopLeaseAsync(key, ct), ct);
            return true;
        }

        _stats.IncFallbackToMemory();
        _current.SetCurrent(_fallback.Name);
        task = _fallback.LPopLeaseAsync(key, ct);
        return true;
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<RedisValueLease> RPopLeaseAsync(string key, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.RPopLeaseAsync(key, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.RPopLeaseAsync(key, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "RPOP (lease)", key);
            return await _fallback.RPopLeaseAsync(key, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public bool TryRPopLeaseAsync(string key, CancellationToken ct, out ValueTask<RedisValueLease> task)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            task = _fallback.RPopLeaseAsync(key, ct);
            return true;
        }

        if (_redis.TryRPopLeaseAsync(key, ct, out var redisTask))
        {
            task = FailOpenTryAsync(redisTask, () => _fallback.RPopLeaseAsync(key, ct), ct);
            return true;
        }

        _stats.IncFallbackToMemory();
        _current.SetCurrent(_fallback.Name);
        task = _fallback.RPopLeaseAsync(key, ct);
        return true;
    }

    // ========== Set Operations ==========

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long> SAddAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.SAddAsync(key, member, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.SAddAsync(key, member, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "SADD", key);
            return await _fallback.SAddAsync(key, member, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long> SRemAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.SRemAsync(key, member, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.SRemAsync(key, member, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "SREM", key);
            return await _fallback.SRemAsync(key, member, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<bool> SIsMemberAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.SIsMemberAsync(key, member, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.SIsMemberAsync(key, member, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "SISMEMBER", key);
            return await _fallback.SIsMemberAsync(key, member, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public bool TrySIsMemberAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct, out ValueTask<bool> task)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            task = _fallback.SIsMemberAsync(key, member, ct);
            return true;
        }

        if (_redis.TrySIsMemberAsync(key, member, ct, out var redisTask))
        {
            task = FailOpenTryAsync(redisTask, () => _fallback.SIsMemberAsync(key, member, ct), ct);
            return true;
        }

        _stats.IncFallbackToMemory();
        _current.SetCurrent(_fallback.Name);
        task = _fallback.SIsMemberAsync(key, member, ct);
        return true;
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<byte[]?[]> SMembersAsync(string key, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.SMembersAsync(key, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.SMembersAsync(key, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "SMEMBERS", key);
            return await _fallback.SMembersAsync(key, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long> SCardAsync(string key, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.SCardAsync(key, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.SCardAsync(key, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "SCARD", key);
            return await _fallback.SCardAsync(key, ct).ConfigureAwait(false);
        }
    }

    // ========== Sorted Set Operations ==========

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long> ZAddAsync(string key, double score, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.ZAddAsync(key, score, member, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.ZAddAsync(key, score, member, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "ZADD", key);
            return await _fallback.ZAddAsync(key, score, member, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long> ZRemAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.ZRemAsync(key, member, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.ZRemAsync(key, member, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "ZREM", key);
            return await _fallback.ZRemAsync(key, member, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long> ZCardAsync(string key, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.ZCardAsync(key, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.ZCardAsync(key, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "ZCARD", key);
            return await _fallback.ZCardAsync(key, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<double?> ZScoreAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.ZScoreAsync(key, member, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.ZScoreAsync(key, member, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "ZSCORE", key);
            return await _fallback.ZScoreAsync(key, member, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long?> ZRankAsync(string key, ReadOnlyMemory<byte> member, bool descending, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.ZRankAsync(key, member, descending, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.ZRankAsync(key, member, descending, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "ZRANK", key);
            return await _fallback.ZRankAsync(key, member, descending, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<double> ZIncrByAsync(string key, double increment, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.ZIncrByAsync(key, increment, member, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.ZIncrByAsync(key, increment, member, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "ZINCRBY", key);
            return await _fallback.ZIncrByAsync(key, increment, member, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<(byte[] Member, double Score)[]> ZRangeWithScoresAsync(string key, long start, long stop, bool descending, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.ZRangeWithScoresAsync(key, start, stop, descending, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.ZRangeWithScoresAsync(key, start, stop, descending, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "ZRANGE", key);
            return await _fallback.ZRangeWithScoresAsync(key, start, stop, descending, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<(byte[] Member, double Score)[]> ZRangeByScoreWithScoresAsync(
        string key,
        double min,
        double max,
        bool descending,
        long? offset,
        long? count,
        CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.ZRangeByScoreWithScoresAsync(key, min, max, descending, offset, count, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.ZRangeByScoreWithScoresAsync(key, min, max, descending, offset, count, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "ZRANGEBYSCORE", key);
            return await _fallback.ZRangeByScoreWithScoresAsync(key, min, max, descending, offset, count, ct).ConfigureAwait(false);
        }
    }

    // ========== JSON Operations ==========

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<byte[]?> JsonGetAsync(string key, string? path, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.JsonGetAsync(key, path, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.JsonGetAsync(key, path, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "JSON.GET", key);
            return await _fallback.JsonGetAsync(key, path, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<RedisValueLease> JsonGetLeaseAsync(string key, string? path, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.JsonGetLeaseAsync(key, path, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.JsonGetLeaseAsync(key, path, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "JSON.GET (lease)", key);
            return await _fallback.JsonGetLeaseAsync(key, path, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public bool TryJsonGetLeaseAsync(string key, string? path, CancellationToken ct, out ValueTask<RedisValueLease> task)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            task = _fallback.JsonGetLeaseAsync(key, path, ct);
            return true;
        }

        if (_redis.TryJsonGetLeaseAsync(key, path, ct, out var redisTask))
        {
            task = FailOpenTryAsync(redisTask, () => _fallback.JsonGetLeaseAsync(key, path, ct), ct);
            return true;
        }

        _stats.IncFallbackToMemory();
        _current.SetCurrent(_fallback.Name);
        task = _fallback.JsonGetLeaseAsync(key, path, ct);
        return true;
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<bool> JsonSetAsync(string key, string? path, ReadOnlyMemory<byte> json, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.JsonSetAsync(key, path, json, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.JsonSetAsync(key, path, json, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "JSON.SET", key);
            return await _fallback.JsonSetAsync(key, path, json, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<bool> JsonSetLeaseAsync(string key, string? path, RedisValueLease json, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(json);

        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.JsonSetLeaseAsync(key, path, json, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.JsonSetLeaseAsync(key, path, json, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "JSON.SET (lease)", key);
            return await _fallback.JsonSetLeaseAsync(key, path, json, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long> JsonDelAsync(string key, string? path, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.JsonDelAsync(key, path, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.JsonDelAsync(key, path, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "JSON.DEL", key);
            return await _fallback.JsonDelAsync(key, path, ct).ConfigureAwait(false);
        }
    }

    // ========== RediSearch / RedisBloom / RedisTimeSeries ==========

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<bool> FtCreateAsync(string index, string prefix, string[] fields, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.FtCreateAsync(index, prefix, fields, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.FtCreateAsync(index, prefix, fields, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex) when (IsRedisIndexAlreadyExists(ex))
        {
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            LogSearchIndexAlreadyExists(_logger, index);
            return false;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisIndexFallback(_logger, ex, "FT.CREATE", index);
            return await _fallback.FtCreateAsync(index, prefix, fields, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<string[]> FtSearchAsync(string index, string query, int? offset, int? count, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.FtSearchAsync(index, query, offset, count, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.FtSearchAsync(index, query, offset, count, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisIndexFallback(_logger, ex, "FT.SEARCH", index);
            return await _fallback.FtSearchAsync(index, query, offset, count, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<bool> BfAddAsync(string key, ReadOnlyMemory<byte> item, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.BfAddAsync(key, item, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.BfAddAsync(key, item, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "BF.ADD", key);
            return await _fallback.BfAddAsync(key, item, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<bool> BfExistsAsync(string key, ReadOnlyMemory<byte> item, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.BfExistsAsync(key, item, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.BfExistsAsync(key, item, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "BF.EXISTS", key);
            return await _fallback.BfExistsAsync(key, item, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<bool> TsCreateAsync(string key, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.TsCreateAsync(key, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.TsCreateAsync(key, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "TS.CREATE", key);
            return await _fallback.TsCreateAsync(key, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long> TsAddAsync(string key, long timestamp, double value, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.TsAddAsync(key, timestamp, value, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.TsAddAsync(key, timestamp, value, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "TS.ADD", key);
            return await _fallback.TsAddAsync(key, timestamp, value, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<(long Timestamp, double Value)[]> TsRangeAsync(string key, long from, long to, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.TsRangeAsync(key, from, to, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.TsRangeAsync(key, from, to, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "TS.RANGE", key);
            return await _fallback.TsRangeAsync(key, from, to, ct).ConfigureAwait(false);
        }
    }

    // ========== Scan Operations ==========

    /// <summary>
    /// Executes value.
    /// </summary>
    public IAsyncEnumerable<string> ScanAsync(string? pattern = null, int pageSize = 128, CancellationToken ct = default)
        => StreamWithFallback("SCAN", () => _redis.ScanAsync(pattern, pageSize, ct), () => _fallback.ScanAsync(pattern, pageSize, ct), ct);

    /// <summary>
    /// Executes value.
    /// </summary>
    public IAsyncEnumerable<byte[]> SScanAsync(string key, string? pattern = null, int pageSize = 128, CancellationToken ct = default)
        => StreamWithFallback("SSCAN", () => _redis.SScanAsync(key, pattern, pageSize, ct), () => _fallback.SScanAsync(key, pattern, pageSize, ct), ct);

    public IAsyncEnumerable<(string Field, byte[] Value)> HScanAsync(string key, string? pattern = null, int pageSize = 128, CancellationToken ct = default)
        => StreamWithFallback("HSCAN", () => _redis.HScanAsync(key, pattern, pageSize, ct), () => _fallback.HScanAsync(key, pattern, pageSize, ct), ct);

    public IAsyncEnumerable<(byte[] Member, double Score)> ZScanAsync(string key, string? pattern = null, int pageSize = 128, CancellationToken ct = default)
        => StreamWithFallback("ZSCAN", () => _redis.ZScanAsync(key, pattern, pageSize, ct), () => _fallback.ZScanAsync(key, pattern, pageSize, ct), ct);

    // ========== Server Commands ==========

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<string> PingAsync(CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.PingAsync(ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.PingAsync(ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisOperationFallback(_logger, ex, "PING");
            return await _fallback.PingAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<string[]> ModuleListAsync(CancellationToken ct)
    {
        try
        {
            if (_breaker.Enabled && _breakerState.IsOpen)
                LogBreakerOpenCapabilityDiscovery(_logger, "MODULE LIST");

            return await _redis.ModuleListAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogRedisCapabilityDiscoveryFailed(_logger, ex, "MODULE LIST");
            throw;
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<bool> ExpireAsync(string key, TimeSpan ttl, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogBreakerOpenExecutingFallback(_logger, "EXPIRE");
            return await _fallback.ExpireAsync(key, ttl, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.ExpireAsync(key, ttl, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "EXPIRE", key);
            return await _fallback.ExpireAsync(key, ttl, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<byte[]?> LIndexAsync(string key, long index, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogBreakerOpenExecutingFallback(_logger, "LINDEX");
            return await _fallback.LIndexAsync(key, index, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.LIndexAsync(key, index, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "LINDEX", key);
            return await _fallback.LIndexAsync(key, index, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets value.
    /// </summary>
    public async ValueTask<byte[]?> GetRangeAsync(string key, long start, long end, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogBreakerOpenExecutingFallback(_logger, "GETRANGE");
            return await _fallback.GetRangeAsync(key, start, end, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.GetRangeAsync(key, start, end, ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            LogRedisKeyFallback(_logger, ex, "GETRANGE", key);
            return await _fallback.GetRangeAsync(key, start, end, ct).ConfigureAwait(false);
        }
    }

    [LoggerMessage(
        EventId = 5301,
        Level = LogLevel.Warning,
        Message = "Transient error in {Operation}, retrying ({Attempt}/{MaxRetries}) after {DelayMs}ms")]
    private static partial void LogTransientRetry(
        ILogger logger,
        Exception exception,
        string operation,
        int attempt,
        int maxRetries,
        double delayMs);

    [LoggerMessage(
        EventId = 5302,
        Level = LogLevel.Warning,
        Message = "Redis {Operation} failed; falling back to fallback")]
    private static partial void LogRedisOperationFallback(ILogger logger, Exception exception, string operation);

    [LoggerMessage(
        EventId = 5303,
        Level = LogLevel.Warning,
        Message = "Redis {Operation} failed for key {Key}; falling back to fallback")]
    private static partial void LogRedisKeyFallback(ILogger logger, Exception exception, string operation, string key);

    [LoggerMessage(
        EventId = 5304,
        Level = LogLevel.Warning,
        Message = "Redis {Operation} failed for key {Key} field {Field}; falling back to fallback")]
    private static partial void LogRedisKeyFieldFallback(ILogger logger, Exception exception, string operation, string key, string field);

    [LoggerMessage(
        EventId = 5305,
        Level = LogLevel.Warning,
        Message = "Redis {Operation} failed for index {Index}; falling back to fallback")]
    private static partial void LogRedisIndexFallback(ILogger logger, Exception exception, string operation, string index);

    [LoggerMessage(
        EventId = 5306,
        Level = LogLevel.Debug,
        Message = "Circuit breaker open. Using fallback for {Operation} on key {Key}")]
    private static partial void LogBreakerOpenUsingFallback(ILogger logger, string operation, string key);

    [LoggerMessage(
        EventId = 5307,
        Level = LogLevel.Debug,
        Message = "Redis FT.CREATE index {Index} already exists; treating as ready.")]
    private static partial void LogSearchIndexAlreadyExists(ILogger logger, string index);

    [LoggerMessage(
        EventId = 5308,
        Level = LogLevel.Warning,
        Message = "Redis {Operation} failed; fallback backend does not provide Redis module metadata")]
    private static partial void LogRedisCapabilityDiscoveryFailed(ILogger logger, Exception exception, string operation);

    [LoggerMessage(
        EventId = 5309,
        Level = LogLevel.Debug,
        Message = "Circuit breaker open. Executing {Command} directly against Redis for capability discovery")]
    private static partial void LogBreakerOpenCapabilityDiscovery(ILogger logger, string command);

    [LoggerMessage(
        EventId = 5310,
        Level = LogLevel.Debug,
        Message = "Circuit breaker open. Executing {Command} against fallback backend")]
    private static partial void LogBreakerOpenExecutingFallback(ILogger logger, string command);

    /// <summary>
    /// Asynchronously releases resources used by the current instance.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _redis.DisposeAsync().ConfigureAwait(false);
        await _fallback.DisposeAsync().ConfigureAwait(false);
    }
}
