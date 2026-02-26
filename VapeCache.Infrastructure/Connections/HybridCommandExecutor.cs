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
internal sealed class HybridCommandExecutor : IRedisCommandExecutor, IRedisMultiplexerDiagnostics
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

    public IRedisBatch CreateBatch()
        => new RedisBatch(this);

    public RedisAutoscalerSnapshot GetAutoscalerSnapshot()
        => _redis.GetAutoscalerSnapshot();

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
                _logger.LogWarning(ex, "Transient error in {Operation}, retrying ({Attempt}/{MaxRetries}) after {Delay}ms",
                    operationName, attempt, maxRetries, delay.TotalMilliseconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransientError(Exception ex)
    {
        // Identify transient errors that are worth retrying
        return ex.Message.Contains("Ring dequeue failed") ||
               ex.Message.Contains("timeout") ||
               ex.Message.Contains("temporarily unavailable") ||
               ex is TimeoutException ||
               ex is OperationCanceledException && !ex.Message.Contains("user");
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
        _logger.LogWarning(error, "Redis {Op} failed; falling back to fallback", op);

        await foreach (var item in fallback().WithCancellation(ct).ConfigureAwait(false))
            yield return item;
    }

    // ========== Simple Key-Value Operations ==========

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
            _logger.LogWarning(ex, "Redis GET failed for key {Key}; falling back to fallback", key);
            return await _fallback.GetAsync(key, ct).ConfigureAwait(false);
        }
    }

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
            _logger.LogWarning(ex, "Redis GETEX failed for key {Key}; falling back to fallback", key);
            return await _fallback.GetExAsync(key, ttl, ct).ConfigureAwait(false);
        }
    }

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
            _logger.LogWarning(ex, "Redis MGET failed; falling back to fallback");
            return await _fallback.MGetAsync(keys, ct).ConfigureAwait(false);
        }
    }

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
            _logger.LogWarning(ex, "Redis SET failed for key {Key}; falling back to fallback", key);
            return await _fallback.SetAsync(key, value, ttl, ct).ConfigureAwait(false);
        }
    }

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
            _logger.LogWarning(ex, "Redis MSET failed; falling back to fallback");
            return await _fallback.MSetAsync(items, ct).ConfigureAwait(false);
        }
    }

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
            _logger.LogWarning(ex, "Redis DEL failed for key {Key}; falling back to fallback", key);
            return await _fallback.DeleteAsync(key, ct).ConfigureAwait(false);
        }
    }

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
            _logger.LogWarning(ex, "Redis TTL failed for key {Key}; falling back to fallback", key);
            return await _fallback.TtlSecondsAsync(key, ct).ConfigureAwait(false);
        }
    }

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
            _logger.LogWarning(ex, "Redis PTTL failed for key {Key}; falling back to fallback", key);
            return await _fallback.PTtlMillisecondsAsync(key, ct).ConfigureAwait(false);
        }
    }

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
            _logger.LogWarning(ex, "Redis UNLINK failed for key {Key}; falling back to fallback", key);
            return await _fallback.UnlinkAsync(key, ct).ConfigureAwait(false);
        }
    }

    // ========== Lease-Based Reads ==========

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
            _logger.LogWarning(ex, "Redis GET (lease) failed for key {Key}; falling back to fallback", key);
            return await _fallback.GetLeaseAsync(key, ct).ConfigureAwait(false);
        }
    }

    public bool TryGetLeaseAsync(string key, CancellationToken ct, out ValueTask<RedisValueLease> task)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            task = _fallback.GetLeaseAsync(key, ct);
            return true;
        }

        if (_redis.TryGetLeaseAsync(key, ct, out task))
        {
            task = FailOpenTryAsync(task, () => _fallback.GetLeaseAsync(key, ct), ct);
            return true;
        }

        _stats.IncFallbackToMemory();
        _current.SetCurrent(_fallback.Name);
        task = _fallback.GetLeaseAsync(key, ct);
        return true;
    }

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
            _logger.LogWarning(ex, "Redis GETEX (lease) failed for key {Key}; falling back to fallback", key);
            return await _fallback.GetExLeaseAsync(key, ttl, ct).ConfigureAwait(false);
        }
    }

    // ========== Hash Operations ==========

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
            _logger.LogWarning(ex, "Redis HSET failed for key {Key} field {Field}; falling back to fallback", key, field);
            return await _fallback.HSetAsync(key, field, value, ct).ConfigureAwait(false);
        }
    }

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
            _logger.LogWarning(ex, "Redis HGET failed for key {Key} field {Field}; falling back to fallback", key, field);
            return await _fallback.HGetAsync(key, field, ct).ConfigureAwait(false);
        }
    }

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
            _logger.LogWarning(ex, "Redis HMGET failed for key {Key}; falling back to fallback", key);
            return await _fallback.HMGetAsync(key, fields, ct).ConfigureAwait(false);
        }
    }

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
            _logger.LogWarning(ex, "Redis HGET (lease) failed for key {Key} field {Field}; falling back to fallback", key, field);
            return await _fallback.HGetLeaseAsync(key, field, ct).ConfigureAwait(false);
        }
    }

    // ========== List Operations ==========

    public async ValueTask<long> LPushAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            _logger.LogDebug("Circuit breaker open. Using fallback for LPUSH on key {Key}", key);
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
            _logger.LogWarning(ex, "Redis LPUSH failed for key {Key}; falling back to fallback", key);
            return await _fallback.LPushAsync(key, value, ct).ConfigureAwait(false);
        }
    }

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
            _logger.LogWarning(ex, "Redis RPUSH failed for key {Key}; falling back to fallback", key);
            return await _fallback.RPushAsync(key, value, ct).ConfigureAwait(false);
        }
    }

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
            _logger.LogWarning(ex, "Redis RPUSH (many) failed for key {Key}; falling back to fallback", key);
            return await _fallback.RPushManyAsync(key, values, count, ct).ConfigureAwait(false);
        }
    }

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
            _logger.LogWarning(ex, "Redis LPOP failed for key {Key}; falling back to fallback", key);
            return await _fallback.LPopAsync(key, ct).ConfigureAwait(false);
        }
    }

    public bool TryHGetAsync(string key, string field, CancellationToken ct, out ValueTask<byte[]?> task)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            task = _fallback.HGetAsync(key, field, ct);
            return true;
        }

        if (_redis.TryHGetAsync(key, field, ct, out task))
        {
            task = FailOpenTryAsync(task, () => _fallback.HGetAsync(key, field, ct), ct);
            return true;
        }

        _stats.IncFallbackToMemory();
        _current.SetCurrent(_fallback.Name);
        task = _fallback.HGetAsync(key, field, ct);
        return true;
    }

    public bool TryGetExLeaseAsync(string key, TimeSpan? ttl, CancellationToken ct, out ValueTask<RedisValueLease> task)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            task = _fallback.GetExLeaseAsync(key, ttl, ct);
            return true;
        }

        if (_redis.TryGetExLeaseAsync(key, ttl, ct, out task))
        {
            task = FailOpenTryAsync(task, () => _fallback.GetExLeaseAsync(key, ttl, ct), ct);
            return true;
        }

        _stats.IncFallbackToMemory();
        _current.SetCurrent(_fallback.Name);
        task = _fallback.GetExLeaseAsync(key, ttl, ct);
        return true;
    }

    public bool TrySetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan? ttl, CancellationToken ct, out ValueTask<bool> task)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            task = _fallback.SetAsync(key, value, ttl, ct);
            return true;
        }

        if (_redis.TrySetAsync(key, value, ttl, ct, out task))
        {
            task = FailOpenTryAsync(task, () => _fallback.SetAsync(key, value, ttl, ct), ct);
            return true;
        }

        _stats.IncFallbackToMemory();
        _current.SetCurrent(_fallback.Name);
        task = _fallback.SetAsync(key, value, ttl, ct);
        return true;
    }

    public bool TryGetExAsync(string key, TimeSpan? ttl, CancellationToken ct, out ValueTask<byte[]?> task)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            task = _fallback.GetExAsync(key, ttl, ct);
            return true;
        }

        if (_redis.TryGetExAsync(key, ttl, ct, out task))
        {
            task = FailOpenTryAsync(task, () => _fallback.GetExAsync(key, ttl, ct), ct);
            return true;
        }

        _stats.IncFallbackToMemory();
        _current.SetCurrent(_fallback.Name);
        task = _fallback.GetExAsync(key, ttl, ct);
        return true;
    }

    public bool TryGetAsync(string key, CancellationToken ct, out ValueTask<byte[]?> task)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            task = _fallback.GetAsync(key, ct);
            return true;
        }

        if (_redis.TryGetAsync(key, ct, out task))
        {
            task = FailOpenTryAsync(task, () => _fallback.GetAsync(key, ct), ct);
            return true;
        }

        _stats.IncFallbackToMemory();
        _current.SetCurrent(_fallback.Name);
        task = _fallback.GetAsync(key, ct);
        return true;
    }

    public bool TryLPopAsync(string key, CancellationToken ct, out ValueTask<byte[]?> task)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            task = _fallback.LPopAsync(key, ct);
            return true;
        }

        if (_redis.TryLPopAsync(key, ct, out task))
        {
            task = FailOpenTryAsync(task, () => _fallback.LPopAsync(key, ct), ct);
            return true;
        }

        _stats.IncFallbackToMemory();
        _current.SetCurrent(_fallback.Name);
        task = _fallback.LPopAsync(key, ct);
        return true;
    }

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
            _logger.LogWarning(ex, "Redis RPOP failed for key {Key}; falling back to fallback", key);
            return await _fallback.RPopAsync(key, ct).ConfigureAwait(false);
        }
    }

    public bool TryRPopAsync(string key, CancellationToken ct, out ValueTask<byte[]?> task)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            task = _fallback.RPopAsync(key, ct);
            return true;
        }

        if (_redis.TryRPopAsync(key, ct, out task))
        {
            task = FailOpenTryAsync(task, () => _fallback.RPopAsync(key, ct), ct);
            return true;
        }

        _stats.IncFallbackToMemory();
        _current.SetCurrent(_fallback.Name);
        task = _fallback.RPopAsync(key, ct);
        return true;
    }

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
            _logger.LogWarning(ex, "Redis LRANGE failed for key {Key}; falling back to fallback", key);
            return await _fallback.LRangeAsync(key, start, stop, ct).ConfigureAwait(false);
        }
    }

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
            _logger.LogWarning(ex, "Redis LLEN failed for key {Key}; falling back to fallback", key);
            return await _fallback.LLenAsync(key, ct).ConfigureAwait(false);
        }
    }

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
            _logger.LogWarning(ex, "Redis LPOP (lease) failed for key {Key}; falling back to fallback", key);
            return await _fallback.LPopLeaseAsync(key, ct).ConfigureAwait(false);
        }
    }

    public bool TryLPopLeaseAsync(string key, CancellationToken ct, out ValueTask<RedisValueLease> task)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            task = _fallback.LPopLeaseAsync(key, ct);
            return true;
        }

        if (_redis.TryLPopLeaseAsync(key, ct, out task))
        {
            task = FailOpenTryAsync(task, () => _fallback.LPopLeaseAsync(key, ct), ct);
            return true;
        }

        _stats.IncFallbackToMemory();
        _current.SetCurrent(_fallback.Name);
        task = _fallback.LPopLeaseAsync(key, ct);
        return true;
    }

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
            _logger.LogWarning(ex, "Redis RPOP (lease) failed for key {Key}; falling back to fallback", key);
            return await _fallback.RPopLeaseAsync(key, ct).ConfigureAwait(false);
        }
    }

    public bool TryRPopLeaseAsync(string key, CancellationToken ct, out ValueTask<RedisValueLease> task)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            task = _fallback.RPopLeaseAsync(key, ct);
            return true;
        }

        if (_redis.TryRPopLeaseAsync(key, ct, out task))
        {
            task = FailOpenTryAsync(task, () => _fallback.RPopLeaseAsync(key, ct), ct);
            return true;
        }

        _stats.IncFallbackToMemory();
        _current.SetCurrent(_fallback.Name);
        task = _fallback.RPopLeaseAsync(key, ct);
        return true;
    }

    // ========== Set Operations ==========

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
            _logger.LogWarning(ex, "Redis SADD failed for key {Key}; falling back to fallback", key);
            return await _fallback.SAddAsync(key, member, ct).ConfigureAwait(false);
        }
    }

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
            _logger.LogWarning(ex, "Redis SREM failed for key {Key}; falling back to fallback", key);
            return await _fallback.SRemAsync(key, member, ct).ConfigureAwait(false);
        }
    }

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
            _logger.LogWarning(ex, "Redis SISMEMBER failed for key {Key}; falling back to fallback", key);
            return await _fallback.SIsMemberAsync(key, member, ct).ConfigureAwait(false);
        }
    }

    public bool TrySIsMemberAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct, out ValueTask<bool> task)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            task = _fallback.SIsMemberAsync(key, member, ct);
            return true;
        }

        if (_redis.TrySIsMemberAsync(key, member, ct, out task))
        {
            task = FailOpenTryAsync(task, () => _fallback.SIsMemberAsync(key, member, ct), ct);
            return true;
        }

        _stats.IncFallbackToMemory();
        _current.SetCurrent(_fallback.Name);
        task = _fallback.SIsMemberAsync(key, member, ct);
        return true;
    }

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
            _logger.LogWarning(ex, "Redis SMEMBERS failed for key {Key}; falling back to fallback", key);
            return await _fallback.SMembersAsync(key, ct).ConfigureAwait(false);
        }
    }

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
            _logger.LogWarning(ex, "Redis SCARD failed for key {Key}; falling back to fallback", key);
            return await _fallback.SCardAsync(key, ct).ConfigureAwait(false);
        }
    }

    // ========== Sorted Set Operations ==========

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
            _logger.LogWarning(ex, "Redis ZADD failed for key {Key}; falling back to fallback", key);
            return await _fallback.ZAddAsync(key, score, member, ct).ConfigureAwait(false);
        }
    }

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
            _logger.LogWarning(ex, "Redis ZREM failed for key {Key}; falling back to fallback", key);
            return await _fallback.ZRemAsync(key, member, ct).ConfigureAwait(false);
        }
    }

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
            _logger.LogWarning(ex, "Redis ZCARD failed for key {Key}; falling back to fallback", key);
            return await _fallback.ZCardAsync(key, ct).ConfigureAwait(false);
        }
    }

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
            _logger.LogWarning(ex, "Redis ZSCORE failed for key {Key}; falling back to fallback", key);
            return await _fallback.ZScoreAsync(key, member, ct).ConfigureAwait(false);
        }
    }

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
            _logger.LogWarning(ex, "Redis ZRANK failed for key {Key}; falling back to fallback", key);
            return await _fallback.ZRankAsync(key, member, descending, ct).ConfigureAwait(false);
        }
    }

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
            _logger.LogWarning(ex, "Redis ZINCRBY failed for key {Key}; falling back to fallback", key);
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
            _logger.LogWarning(ex, "Redis ZRANGE failed for key {Key}; falling back to fallback", key);
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
            _logger.LogWarning(ex, "Redis ZRANGEBYSCORE failed for key {Key}; falling back to fallback", key);
            return await _fallback.ZRangeByScoreWithScoresAsync(key, min, max, descending, offset, count, ct).ConfigureAwait(false);
        }
    }

    // ========== JSON Operations ==========

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
            _logger.LogWarning(ex, "Redis JSON.GET failed for key {Key}; falling back to fallback", key);
            return await _fallback.JsonGetAsync(key, path, ct).ConfigureAwait(false);
        }
    }

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
            _logger.LogWarning(ex, "Redis JSON.GET (lease) failed for key {Key}; falling back to fallback", key);
            return await _fallback.JsonGetLeaseAsync(key, path, ct).ConfigureAwait(false);
        }
    }

    public bool TryJsonGetLeaseAsync(string key, string? path, CancellationToken ct, out ValueTask<RedisValueLease> task)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            task = _fallback.JsonGetLeaseAsync(key, path, ct);
            return true;
        }

        if (_redis.TryJsonGetLeaseAsync(key, path, ct, out task))
        {
            task = FailOpenTryAsync(task, () => _fallback.JsonGetLeaseAsync(key, path, ct), ct);
            return true;
        }

        _stats.IncFallbackToMemory();
        _current.SetCurrent(_fallback.Name);
        task = _fallback.JsonGetLeaseAsync(key, path, ct);
        return true;
    }

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
            _logger.LogWarning(ex, "Redis JSON.SET failed for key {Key}; falling back to fallback", key);
            return await _fallback.JsonSetAsync(key, path, json, ct).ConfigureAwait(false);
        }
    }

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
            _logger.LogWarning(ex, "Redis JSON.SET (lease) failed for key {Key}; falling back to fallback", key);
            return await _fallback.JsonSetLeaseAsync(key, path, json, ct).ConfigureAwait(false);
        }
    }

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
            _logger.LogWarning(ex, "Redis JSON.DEL failed for key {Key}; falling back to fallback", key);
            return await _fallback.JsonDelAsync(key, path, ct).ConfigureAwait(false);
        }
    }

    // ========== RediSearch / RedisBloom / RedisTimeSeries ==========

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
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            _logger.LogWarning(ex, "Redis FT.CREATE failed for index {Index}; falling back to fallback", index);
            return await _fallback.FtCreateAsync(index, prefix, fields, ct).ConfigureAwait(false);
        }
    }

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
            _logger.LogWarning(ex, "Redis FT.SEARCH failed for index {Index}; falling back to fallback", index);
            return await _fallback.FtSearchAsync(index, query, offset, count, ct).ConfigureAwait(false);
        }
    }

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
            _logger.LogWarning(ex, "Redis BF.ADD failed for key {Key}; falling back to fallback", key);
            return await _fallback.BfAddAsync(key, item, ct).ConfigureAwait(false);
        }
    }

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
            _logger.LogWarning(ex, "Redis BF.EXISTS failed for key {Key}; falling back to fallback", key);
            return await _fallback.BfExistsAsync(key, item, ct).ConfigureAwait(false);
        }
    }

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
            _logger.LogWarning(ex, "Redis TS.CREATE failed for key {Key}; falling back to fallback", key);
            return await _fallback.TsCreateAsync(key, ct).ConfigureAwait(false);
        }
    }

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
            _logger.LogWarning(ex, "Redis TS.ADD failed for key {Key}; falling back to fallback", key);
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
            _logger.LogWarning(ex, "Redis TS.RANGE failed for key {Key}; falling back to fallback", key);
            return await _fallback.TsRangeAsync(key, from, to, ct).ConfigureAwait(false);
        }
    }

    // ========== Scan Operations ==========

    public IAsyncEnumerable<string> ScanAsync(string? pattern = null, int pageSize = 128, CancellationToken ct = default)
        => StreamWithFallback("SCAN", () => _redis.ScanAsync(pattern, pageSize, ct), () => _fallback.ScanAsync(pattern, pageSize, ct), ct);

    public IAsyncEnumerable<byte[]> SScanAsync(string key, string? pattern = null, int pageSize = 128, CancellationToken ct = default)
        => StreamWithFallback("SSCAN", () => _redis.SScanAsync(key, pattern, pageSize, ct), () => _fallback.SScanAsync(key, pattern, pageSize, ct), ct);

    public IAsyncEnumerable<(string Field, byte[] Value)> HScanAsync(string key, string? pattern = null, int pageSize = 128, CancellationToken ct = default)
        => StreamWithFallback("HSCAN", () => _redis.HScanAsync(key, pattern, pageSize, ct), () => _fallback.HScanAsync(key, pattern, pageSize, ct), ct);

    public IAsyncEnumerable<(byte[] Member, double Score)> ZScanAsync(string key, string? pattern = null, int pageSize = 128, CancellationToken ct = default)
        => StreamWithFallback("ZSCAN", () => _redis.ZScanAsync(key, pattern, pageSize, ct), () => _fallback.ZScanAsync(key, pattern, pageSize, ct), ct);

    // ========== Server Commands ==========

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
            _logger.LogWarning(ex, "Redis PING failed; falling back to fallback");
            return await _fallback.PingAsync(ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<string[]> ModuleListAsync(CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            return await _fallback.ModuleListAsync(ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.ModuleListAsync(ct).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            _logger.LogWarning(ex, "Redis MODULE LIST failed; falling back to fallback");
            return await _fallback.ModuleListAsync(ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<bool> ExpireAsync(string key, TimeSpan ttl, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            _logger.LogDebug("Circuit breaker open. Executing {Command} against fallback backend", "EXPIRE");
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
            _logger.LogWarning(ex, "Redis EXPIRE failed for key {Key}; falling back to fallback", key);
            return await _fallback.ExpireAsync(key, ttl, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<byte[]?> LIndexAsync(string key, long index, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            _logger.LogDebug("Circuit breaker open. Executing {Command} against fallback backend", "LINDEX");
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
            _logger.LogWarning(ex, "Redis LINDEX failed for key {Key}; falling back to fallback", key);
            return await _fallback.LIndexAsync(key, index, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<byte[]?> GetRangeAsync(string key, long start, long end, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent(_fallback.Name);
            _logger.LogDebug("Circuit breaker open. Executing {Command} against fallback backend", "GETRANGE");
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
            _logger.LogWarning(ex, "Redis GETRANGE failed for key {Key}; falling back to fallback", key);
            return await _fallback.GetRangeAsync(key, start, end, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _redis.DisposeAsync().ConfigureAwait(false);
        await _fallback.DisposeAsync().ConfigureAwait(false);
    }
}
