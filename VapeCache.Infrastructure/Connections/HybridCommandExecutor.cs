using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Caching;

namespace VapeCache.Infrastructure.Connections;

/// <summary>
/// Hybrid Redis command executor that automatically falls back to in-memory implementation
/// when Redis is unavailable. Applies circuit breaker pattern to all Redis data structure operations.
/// </summary>
internal sealed class HybridCommandExecutor : IRedisCommandExecutor
{
    private readonly RedisCommandExecutor _redis;
    private readonly InMemoryCommandExecutor _memory;
    private readonly IRedisCircuitBreakerState _breakerState;
    private readonly IRedisFailoverController _breakerController;
    private readonly CacheStats _stats;
    private readonly ICurrentCacheService _current;
    private readonly ILogger<HybridCommandExecutor> _logger;
    private readonly RedisCircuitBreakerOptions _breaker;
    private int _halfOpenProbes;

    public HybridCommandExecutor(
        RedisCommandExecutor redis,
        InMemoryCommandExecutor memory,
        IRedisCircuitBreakerState breakerState,
        IRedisFailoverController breakerController,
        CacheStatsRegistry statsRegistry,
        ICurrentCacheService current,
        IOptions<RedisCircuitBreakerOptions> breakerOptions,
        ILogger<HybridCommandExecutor> logger)
    {
        _redis = redis;
        _memory = memory;
        _breakerState = breakerState;
        _breakerController = breakerController;
        _stats = statsRegistry.GetOrCreate(CacheStatsNames.Hybrid);
        _current = current;
        _logger = logger;
        _breaker = breakerOptions.Value;
    }

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

    // ========== Simple Key-Value Operations ==========

    public async ValueTask<byte[]?> GetAsync(string key, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            return await _memory.GetAsync(key, ct).ConfigureAwait(false);
        }

        using var probe = StartProbe(ct);
        if (probe.Throttled)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            return await _memory.GetAsync(key, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.GetAsync(key, probe.Token).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            _logger.LogWarning(ex, "Redis GET failed for key {Key}; falling back to in-memory", key);
            return await _memory.GetAsync(key, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<byte[]?> GetExAsync(string key, TimeSpan? ttl, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            return await _memory.GetExAsync(key, ttl, ct).ConfigureAwait(false);
        }

        using var probe = StartProbe(ct);
        if (probe.Throttled)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            return await _memory.GetExAsync(key, ttl, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.GetExAsync(key, ttl, probe.Token).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            _logger.LogWarning(ex, "Redis GETEX failed for key {Key}; falling back to in-memory", key);
            return await _memory.GetExAsync(key, ttl, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<byte[]?[]> MGetAsync(string[] keys, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            return await _memory.MGetAsync(keys, ct).ConfigureAwait(false);
        }

        using var probe = StartProbe(ct);
        if (probe.Throttled)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            return await _memory.MGetAsync(keys, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.MGetAsync(keys, probe.Token).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            _logger.LogWarning(ex, "Redis MGET failed; falling back to in-memory");
            return await _memory.MGetAsync(keys, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<bool> SetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan? ttl, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            return await _memory.SetAsync(key, value, ttl, ct).ConfigureAwait(false);
        }

        using var probe = StartProbe(ct);
        if (probe.Throttled)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            return await _memory.SetAsync(key, value, ttl, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.SetAsync(key, value, ttl, probe.Token).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            _logger.LogWarning(ex, "Redis SET failed for key {Key}; falling back to in-memory", key);
            return await _memory.SetAsync(key, value, ttl, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<bool> MSetAsync((string Key, ReadOnlyMemory<byte> Value)[] items, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            return await _memory.MSetAsync(items, ct).ConfigureAwait(false);
        }

        using var probe = StartProbe(ct);
        if (probe.Throttled)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            return await _memory.MSetAsync(items, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.MSetAsync(items, probe.Token).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            _logger.LogWarning(ex, "Redis MSET failed; falling back to in-memory");
            return await _memory.MSetAsync(items, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<bool> DeleteAsync(string key, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            return await _memory.DeleteAsync(key, ct).ConfigureAwait(false);
        }

        using var probe = StartProbe(ct);
        if (probe.Throttled)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            return await _memory.DeleteAsync(key, ct).ConfigureAwait(false);
        }

        try
        {
            var result = await _redis.DeleteAsync(key, probe.Token).ConfigureAwait(false);
            _breakerController.MarkRedisSuccess();
            _current.SetCurrent("redis");
            return result;
        }
        catch (Exception ex)
        {
            _breakerController.MarkRedisFailure();
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            _logger.LogWarning(ex, "Redis DEL failed for key {Key}; falling back to in-memory", key);
            return await _memory.DeleteAsync(key, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<long> TtlSecondsAsync(string key, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            return await _memory.TtlSecondsAsync(key, ct).ConfigureAwait(false);
        }

        using var probe = StartProbe(ct);
        if (probe.Throttled)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            return await _memory.TtlSecondsAsync(key, ct).ConfigureAwait(false);
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
            _current.SetCurrent("memory");
            _logger.LogWarning(ex, "Redis TTL failed for key {Key}; falling back to in-memory", key);
            return await _memory.TtlSecondsAsync(key, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<long> PTtlMillisecondsAsync(string key, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            return await _memory.PTtlMillisecondsAsync(key, ct).ConfigureAwait(false);
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
            _current.SetCurrent("memory");
            _logger.LogWarning(ex, "Redis PTTL failed for key {Key}; falling back to in-memory", key);
            return await _memory.PTtlMillisecondsAsync(key, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<long> UnlinkAsync(string key, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            return await _memory.UnlinkAsync(key, ct).ConfigureAwait(false);
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
            _current.SetCurrent("memory");
            _logger.LogWarning(ex, "Redis UNLINK failed for key {Key}; falling back to in-memory", key);
            return await _memory.UnlinkAsync(key, ct).ConfigureAwait(false);
        }
    }

    // ========== Lease-Based Reads ==========

    public async ValueTask<RedisValueLease> GetLeaseAsync(string key, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            return await _memory.GetLeaseAsync(key, ct).ConfigureAwait(false);
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
            _current.SetCurrent("memory");
            _logger.LogWarning(ex, "Redis GET (lease) failed for key {Key}; falling back to in-memory", key);
            return await _memory.GetLeaseAsync(key, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<RedisValueLease> GetExLeaseAsync(string key, TimeSpan? ttl, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            return await _memory.GetExLeaseAsync(key, ttl, ct).ConfigureAwait(false);
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
            _current.SetCurrent("memory");
            _logger.LogWarning(ex, "Redis GETEX (lease) failed for key {Key}; falling back to in-memory", key);
            return await _memory.GetExLeaseAsync(key, ttl, ct).ConfigureAwait(false);
        }
    }

    // ========== Hash Operations ==========

    public async ValueTask<long> HSetAsync(string key, string field, ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            return await _memory.HSetAsync(key, field, value, ct).ConfigureAwait(false);
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
            _current.SetCurrent("memory");
            _logger.LogWarning(ex, "Redis HSET failed for key {Key} field {Field}; falling back to in-memory", key, field);
            return await _memory.HSetAsync(key, field, value, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<byte[]?> HGetAsync(string key, string field, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            return await _memory.HGetAsync(key, field, ct).ConfigureAwait(false);
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
            _current.SetCurrent("memory");
            _logger.LogWarning(ex, "Redis HGET failed for key {Key} field {Field}; falling back to in-memory", key, field);
            return await _memory.HGetAsync(key, field, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<byte[]?[]> HMGetAsync(string key, string[] fields, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            return await _memory.HMGetAsync(key, fields, ct).ConfigureAwait(false);
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
            _current.SetCurrent("memory");
            _logger.LogWarning(ex, "Redis HMGET failed for key {Key}; falling back to in-memory", key);
            return await _memory.HMGetAsync(key, fields, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<RedisValueLease> HGetLeaseAsync(string key, string field, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            return await _memory.HGetLeaseAsync(key, field, ct).ConfigureAwait(false);
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
            _current.SetCurrent("memory");
            _logger.LogWarning(ex, "Redis HGET (lease) failed for key {Key} field {Field}; falling back to in-memory", key, field);
            return await _memory.HGetLeaseAsync(key, field, ct).ConfigureAwait(false);
        }
    }

    // ========== List Operations ==========

    public async ValueTask<long> LPushAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            _logger.LogInformation("🔓 Circuit breaker is OPEN - using in-memory for LPUSH on key {Key}", key);
            return await _memory.LPushAsync(key, value, ct).ConfigureAwait(false);
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
            _current.SetCurrent("memory");
            _logger.LogWarning(ex, "Redis LPUSH failed for key {Key}; falling back to in-memory", key);
            return await _memory.LPushAsync(key, value, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<long> RPushAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            return await _memory.RPushAsync(key, value, ct).ConfigureAwait(false);
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
            _current.SetCurrent("memory");
            _logger.LogWarning(ex, "Redis RPUSH failed for key {Key}; falling back to in-memory", key);
            return await _memory.RPushAsync(key, value, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<byte[]?> LPopAsync(string key, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            return await _memory.LPopAsync(key, ct).ConfigureAwait(false);
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
            _current.SetCurrent("memory");
            _logger.LogWarning(ex, "Redis LPOP failed for key {Key}; falling back to in-memory", key);
            return await _memory.LPopAsync(key, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<byte[]?> RPopAsync(string key, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            return await _memory.RPopAsync(key, ct).ConfigureAwait(false);
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
            _current.SetCurrent("memory");
            _logger.LogWarning(ex, "Redis RPOP failed for key {Key}; falling back to in-memory", key);
            return await _memory.RPopAsync(key, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<byte[]?[]> LRangeAsync(string key, long start, long stop, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            return await _memory.LRangeAsync(key, start, stop, ct).ConfigureAwait(false);
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
            _current.SetCurrent("memory");
            _logger.LogWarning(ex, "Redis LRANGE failed for key {Key}; falling back to in-memory", key);
            return await _memory.LRangeAsync(key, start, stop, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<long> LLenAsync(string key, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            return await _memory.LLenAsync(key, ct).ConfigureAwait(false);
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
            _current.SetCurrent("memory");
            _logger.LogWarning(ex, "Redis LLEN failed for key {Key}; falling back to in-memory", key);
            return await _memory.LLenAsync(key, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<RedisValueLease> LPopLeaseAsync(string key, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            return await _memory.LPopLeaseAsync(key, ct).ConfigureAwait(false);
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
            _current.SetCurrent("memory");
            _logger.LogWarning(ex, "Redis LPOP (lease) failed for key {Key}; falling back to in-memory", key);
            return await _memory.LPopLeaseAsync(key, ct).ConfigureAwait(false);
        }
    }

    // ========== Set Operations ==========

    public async ValueTask<long> SAddAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            return await _memory.SAddAsync(key, member, ct).ConfigureAwait(false);
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
            _current.SetCurrent("memory");
            _logger.LogWarning(ex, "Redis SADD failed for key {Key}; falling back to in-memory", key);
            return await _memory.SAddAsync(key, member, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<long> SRemAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            return await _memory.SRemAsync(key, member, ct).ConfigureAwait(false);
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
            _current.SetCurrent("memory");
            _logger.LogWarning(ex, "Redis SREM failed for key {Key}; falling back to in-memory", key);
            return await _memory.SRemAsync(key, member, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<bool> SIsMemberAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            return await _memory.SIsMemberAsync(key, member, ct).ConfigureAwait(false);
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
            _current.SetCurrent("memory");
            _logger.LogWarning(ex, "Redis SISMEMBER failed for key {Key}; falling back to in-memory", key);
            return await _memory.SIsMemberAsync(key, member, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<byte[]?[]> SMembersAsync(string key, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            return await _memory.SMembersAsync(key, ct).ConfigureAwait(false);
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
            _current.SetCurrent("memory");
            _logger.LogWarning(ex, "Redis SMEMBERS failed for key {Key}; falling back to in-memory", key);
            return await _memory.SMembersAsync(key, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<long> SCardAsync(string key, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            return await _memory.SCardAsync(key, ct).ConfigureAwait(false);
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
            _current.SetCurrent("memory");
            _logger.LogWarning(ex, "Redis SCARD failed for key {Key}; falling back to in-memory", key);
            return await _memory.SCardAsync(key, ct).ConfigureAwait(false);
        }
    }

    // ========== Server Commands ==========

    public async ValueTask<string> PingAsync(CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            return await _memory.PingAsync(ct).ConfigureAwait(false);
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
            _current.SetCurrent("memory");
            _logger.LogWarning(ex, "Redis PING failed; falling back to in-memory");
            return await _memory.PingAsync(ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<string[]> ModuleListAsync(CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            return await _memory.ModuleListAsync(ct).ConfigureAwait(false);
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
            _current.SetCurrent("memory");
            _logger.LogWarning(ex, "Redis MODULE LIST failed; falling back to in-memory");
            return await _memory.ModuleListAsync(ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<bool> ExpireAsync(string key, TimeSpan ttl, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            _logger.LogWarning("Redis breaker open: executing {Command} against in-memory backend", "EXPIRE");
            return await _memory.ExpireAsync(key, ttl, ct).ConfigureAwait(false);
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
            _current.SetCurrent("memory");
            _logger.LogWarning(ex, "Redis EXPIRE failed for key {Key}; falling back to in-memory", key);
            return await _memory.ExpireAsync(key, ttl, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<byte[]?> LIndexAsync(string key, long index, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            _logger.LogWarning("Redis breaker open: executing {Command} against in-memory backend", "LINDEX");
            return await _memory.LIndexAsync(key, index, ct).ConfigureAwait(false);
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
            _current.SetCurrent("memory");
            _logger.LogWarning(ex, "Redis LINDEX failed for key {Key}; falling back to in-memory", key);
            return await _memory.LIndexAsync(key, index, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<byte[]?> GetRangeAsync(string key, long start, long end, CancellationToken ct)
    {
        if (_breaker.Enabled && _breakerState.IsOpen)
        {
            _stats.IncFallbackToMemory();
            _current.SetCurrent("memory");
            _logger.LogWarning("Redis breaker open: executing {Command} against in-memory backend", "GETRANGE");
            return await _memory.GetRangeAsync(key, start, end, ct).ConfigureAwait(false);
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
            _current.SetCurrent("memory");
            _logger.LogWarning(ex, "Redis GETRANGE failed for key {Key}; falling back to in-memory", key);
            return await _memory.GetRangeAsync(key, start, end, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _redis.DisposeAsync().ConfigureAwait(false);
        await _memory.DisposeAsync().ConfigureAwait(false);
    }
}
