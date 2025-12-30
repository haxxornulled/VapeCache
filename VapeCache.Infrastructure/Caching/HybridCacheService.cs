using System.Buffers;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;

namespace VapeCache.Infrastructure.Caching;

/// <summary>
/// Hybrid cache implementation that combines Redis for distributed caching with
/// automatic failover to in-memory caching when Redis is unavailable.
/// Uses a circuit breaker pattern to detect failures and gradually recover.
/// </summary>
internal sealed class HybridCacheService(
    RedisCacheService redis,
    InMemoryCacheService memory,
    ICurrentCacheService current,
    TimeProvider timeProvider,
    IOptions<RedisCircuitBreakerOptions> breakerOptions,
    CacheStatsRegistry statsRegistry,
    ILogger<HybridCacheService> logger,
    IRedisReconciliationService? reconciliation = null) : ICacheService
    , IRedisCircuitBreakerState
    , IRedisFailoverController
{
    /// <inheritdoc />
    public string Name => "hybrid";

    private readonly CacheStats _stats = statsRegistry.GetOrCreate(CacheStatsNames.Hybrid);
    private readonly RedisCircuitBreakerOptions _breaker = breakerOptions.Value;
    private int _failures;
    private long _openUntilTicks;
    private int _halfOpenProbeInFlight;
    private int _forcedOpen;
    private string? _forcedReason;
    private int _openAttempts;
    private int _reconcileInFlight;

    public bool Enabled => _breaker.Enabled;
    public bool IsOpen => _breaker.Enabled && (IsForcedOpen || (Volatile.Read(ref _openUntilTicks) != 0 && !IsRedisAllowedNow()));
    public int ConsecutiveFailures => Volatile.Read(ref _failures);
    public TimeSpan? OpenRemaining => _breaker.Enabled ? GetOpenRemaining() : null;
    public bool HalfOpenProbeInFlight => Volatile.Read(ref _halfOpenProbeInFlight) != 0;

    public bool IsForcedOpen => Volatile.Read(ref _forcedOpen) != 0;
    public string? Reason => Volatile.Read(ref _forcedReason);

    private bool IsRedisAllowedNow()
    {
        if (!_breaker.Enabled) return true;
        if (IsForcedOpen) return false;
        var openUntil = Volatile.Read(ref _openUntilTicks);
        if (openUntil == 0) return true; // closed
        return timeProvider.GetTimestamp() >= openUntil;
    }

    public void MarkRedisSuccess()
    {
        if (!_breaker.Enabled) return;
        var wasOpen = Volatile.Read(ref _openUntilTicks) != 0;
        Volatile.Write(ref _failures, 0);
        Volatile.Write(ref _openUntilTicks, 0);
        Volatile.Write(ref _openAttempts, 0);
        if (!IsForcedOpen && wasOpen)
            TryStartReconciliation();
    }

    public void MarkRedisFailure()
    {
        if (!_breaker.Enabled) return;
        var failures = Interlocked.Increment(ref _failures);
        if (failures >= Math.Max(1, _breaker.ConsecutiveFailuresToOpen))
        {
            var attempts = Interlocked.Increment(ref _openAttempts);
            var breakDuration = _breaker.BreakDuration;
            if (_breaker.UseExponentialBackoff)
            {
                var scaled = _breaker.BreakDuration.TotalSeconds * Math.Pow(2, Math.Max(0, attempts - 1));
                var capped = Math.Min(_breaker.MaxBreakDuration.TotalSeconds, scaled);
                breakDuration = TimeSpan.FromSeconds(capped);
            }

            // If we've exceeded max retries, hold the breaker open indefinitely
            if (_breaker.MaxConsecutiveRetries > 0 && attempts > _breaker.MaxConsecutiveRetries)
            {
                Volatile.Write(ref _openUntilTicks, long.MaxValue);
                logger.LogWarning("Redis circuit breaker reached MaxConsecutiveRetries ({Attempts}); holding open indefinitely until manual reset.", attempts);
            }
            else
            {
                var until = AddDurationToTimestamp(timeProvider.GetTimestamp(), breakDuration);
                Volatile.Write(ref _openUntilTicks, until);
            }
            if (failures == _breaker.ConsecutiveFailuresToOpen)
            {
                _stats.IncBreakerOpened();
                CacheTelemetry.RedisBreakerOpened.Add(1, new TagList { { "backend", Name } });
                logger.LogWarning("⚡ CIRCUIT BREAKER OPENED after {Failures} consecutive failures. Switching to in-memory mode for {Duration} seconds.", failures, breakDuration.TotalSeconds);
                System.Console.WriteLine($"\n🔥🔥🔥 ⚡ CIRCUIT BREAKER OPENED! Failures={failures}, switching to IN-MEMORY mode for {breakDuration.TotalSeconds} seconds 🔥🔥🔥\n");
            }
        }
    }

    private long AddDurationToTimestamp(long timestamp, TimeSpan duration)
    {
        var freq = timeProvider.TimestampFrequency;
        var delta = (long)Math.Max(1, duration.TotalSeconds * freq);

        // Protect against overflow - if adding duration would overflow, cap at max value
        if (delta > 0 && timestamp > long.MaxValue - delta)
            return long.MaxValue;

        return timestamp + delta;
    }

    private TimeSpan? GetOpenRemaining()
    {
        if (IsForcedOpen) return null;
        var until = Volatile.Read(ref _openUntilTicks);
        if (until == 0) return null;
        var now = timeProvider.GetTimestamp();
        var remainingTicks = until - now;
        if (remainingTicks <= 0) return TimeSpan.Zero;
        var seconds = remainingTicks / (double)timeProvider.TimestampFrequency;
        return TimeSpan.FromSeconds(seconds);
    }

    public async ValueTask<byte[]?> GetAsync(string key, CancellationToken ct)
    {
        _stats.IncGet();
        CacheTelemetry.GetCalls.Add(1, new TagList { { "backend", Name } });
        var start = Stopwatch.GetTimestamp();

        var probeTaken = false;
        try
        {
            if (_breaker.Enabled && !IsRedisAllowedNow())
            {
                current.SetCurrent(memory.Name);
                _stats.IncFallbackToMemory();
                CacheTelemetry.FallbackToMemory.Add(1, new TagList { { "backend", Name }, { "reason", "breaker_open" } });

                var bytes = await memory.GetAsync(key, ct).ConfigureAwait(false);
                if (bytes is null) { _stats.IncMiss(); CacheTelemetry.Misses.Add(1, new TagList { { "backend", Name } }); }
                else { _stats.IncHit(); CacheTelemetry.Hits.Add(1, new TagList { { "backend", Name } }); }
                return bytes;
            }

            try
            {
                // Half-open state: allow limited concurrent Redis probes during recovery
                if (_breaker.Enabled && Volatile.Read(ref _openUntilTicks) != 0)
                {
                    var probeNum = Interlocked.Increment(ref _halfOpenProbeInFlight);
                    probeTaken = true;

                    if (probeNum > _breaker.MaxHalfOpenProbes)
                    {
                        Interlocked.Decrement(ref _halfOpenProbeInFlight);
                        probeTaken = false;

                        current.SetCurrent(memory.Name);
                        _stats.IncFallbackToMemory();
                        CacheTelemetry.FallbackToMemory.Add(1, new TagList { { "backend", Name }, { "reason", "half_open_busy" } });
                        return await memory.GetAsync(key, ct).ConfigureAwait(false);
                    }
                }

                using var probeCts = probeTaken
                    ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                    : null;
                if (probeCts is not null && _breaker.HalfOpenProbeTimeout > TimeSpan.Zero)
                    probeCts.CancelAfter(_breaker.HalfOpenProbeTimeout);

                var v = await redis.GetAsync(key, probeCts?.Token ?? ct).ConfigureAwait(false);
                MarkRedisSuccess();
                current.SetCurrent(redis.Name);
                if (v is not null)
                {
                    _stats.IncHit();
                    CacheTelemetry.Hits.Add(1, new TagList { { "backend", Name } });
                    return v;
                }
            }
            catch (Exception ex)
            {
                MarkRedisFailure();
                current.SetCurrent(memory.Name);
                _stats.IncFallbackToMemory();
                CacheTelemetry.FallbackToMemory.Add(1, new TagList { { "backend", Name }, { "reason", "redis_error" } });
                logger.LogWarning(ex, "Redis GET failed; falling back to memory.");
            }

            var fallbackBytes = await memory.GetAsync(key, ct).ConfigureAwait(false);
            if (fallbackBytes is null) { _stats.IncMiss(); CacheTelemetry.Misses.Add(1, new TagList { { "backend", Name } }); }
            else { _stats.IncHit(); CacheTelemetry.Hits.Add(1, new TagList { { "backend", Name } }); }
            return fallbackBytes;
        }
        finally
        {
            if (probeTaken)
                Interlocked.Decrement(ref _halfOpenProbeInFlight);
            CacheTelemetry.OpMs.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds, new TagList { { "backend", Name }, { "op", "get" } });
        }
    }

    public async ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CacheEntryOptions options, CancellationToken ct)
    {
        _stats.IncSet();
        CacheTelemetry.SetCalls.Add(1, new TagList { { "backend", Name } });
        var start = Stopwatch.GetTimestamp();

        var probeTaken = false;
        try
        {
            if (_breaker.Enabled && !IsRedisAllowedNow())
            {
                current.SetCurrent(memory.Name);
                _stats.IncFallbackToMemory();
                CacheTelemetry.FallbackToMemory.Add(1, new TagList { { "backend", Name }, { "reason", "breaker_open" } });
                await memory.SetAsync(key, value, options, ct).ConfigureAwait(false);

                // Track write for reconciliation when Redis recovers
                reconciliation?.TrackWrite(key, value, options.Ttl);
                return;
            }

            try
            {
                // Half-open state: allow limited concurrent Redis probes during recovery
                if (_breaker.Enabled && Volatile.Read(ref _openUntilTicks) != 0)
                {
                    var probeNum = Interlocked.Increment(ref _halfOpenProbeInFlight);
                    probeTaken = true;

                    if (probeNum > _breaker.MaxHalfOpenProbes)
                    {
                        Interlocked.Decrement(ref _halfOpenProbeInFlight);
                        probeTaken = false;

                        current.SetCurrent(memory.Name);
                        _stats.IncFallbackToMemory();
                        CacheTelemetry.FallbackToMemory.Add(1, new TagList { { "backend", Name }, { "reason", "half_open_busy" } });
                        await memory.SetAsync(key, value, options, ct).ConfigureAwait(false);

                        // Track write for reconciliation when Redis recovers
                        reconciliation?.TrackWrite(key, value, options.Ttl);
                        return;
                    }
                }

                using var probeCts = probeTaken
                    ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                    : null;
                if (probeCts is not null && _breaker.HalfOpenProbeTimeout > TimeSpan.Zero)
                    probeCts.CancelAfter(_breaker.HalfOpenProbeTimeout);

                await redis.SetAsync(key, value, options, probeCts?.Token ?? ct).ConfigureAwait(false);
                MarkRedisSuccess();
                current.SetCurrent(redis.Name);
            }
            catch (Exception ex)
            {
                MarkRedisFailure();
                current.SetCurrent(memory.Name);
                _stats.IncFallbackToMemory();
                CacheTelemetry.FallbackToMemory.Add(1, new TagList { { "backend", Name }, { "reason", "redis_error" } });
                logger.LogWarning(ex, "Redis SET failed; writing to memory.");
                await memory.SetAsync(key, value, options, ct).ConfigureAwait(false);
                reconciliation?.TrackWrite(key, value, options.Ttl);
            }
        }
        finally
        {
            if (probeTaken)
                Interlocked.Decrement(ref _halfOpenProbeInFlight);
            CacheTelemetry.OpMs.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds, new TagList { { "backend", Name }, { "op", "set" } });
        }
    }

    public async ValueTask<bool> RemoveAsync(string key, CancellationToken ct)
    {
        _stats.IncRemove();
        CacheTelemetry.RemoveCalls.Add(1, new TagList { { "backend", Name } });
        var start = Stopwatch.GetTimestamp();
        var ok = await memory.RemoveAsync(key, ct).ConfigureAwait(false);
        var probeTaken = false;
        try
        {
            if (_breaker.Enabled && !IsRedisAllowedNow())
            {
                current.SetCurrent(memory.Name);
                _stats.IncFallbackToMemory();
                CacheTelemetry.FallbackToMemory.Add(1, new TagList { { "backend", Name }, { "reason", "breaker_open" } });
                reconciliation?.TrackDelete(key);
                return ok;
            }

            try
            {
                // Half-open state: allow limited concurrent Redis probes during recovery
                if (_breaker.Enabled && Volatile.Read(ref _openUntilTicks) != 0)
                {
                    var probeNum = Interlocked.Increment(ref _halfOpenProbeInFlight);
                    probeTaken = true;

                    if (probeNum > _breaker.MaxHalfOpenProbes)
                    {
                        Interlocked.Decrement(ref _halfOpenProbeInFlight);
                        probeTaken = false;

                        current.SetCurrent(memory.Name);
                        _stats.IncFallbackToMemory();
                        CacheTelemetry.FallbackToMemory.Add(1, new TagList { { "backend", Name }, { "reason", "half_open_busy" } });
                        reconciliation?.TrackDelete(key);
                        return ok;
                    }
                }

                using var probeCts = probeTaken
                    ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                    : null;
                if (probeCts is not null && _breaker.HalfOpenProbeTimeout > TimeSpan.Zero)
                    probeCts.CancelAfter(_breaker.HalfOpenProbeTimeout);

                var rok = await redis.RemoveAsync(key, probeCts?.Token ?? ct).ConfigureAwait(false);
                MarkRedisSuccess();
                current.SetCurrent(redis.Name);
                return ok || rok;
            }
            catch (Exception ex)
            {
                MarkRedisFailure();
                current.SetCurrent(memory.Name);
                _stats.IncFallbackToMemory();
                CacheTelemetry.FallbackToMemory.Add(1, new TagList { { "backend", Name }, { "reason", "redis_error" } });
                logger.LogWarning(ex, "Redis DEL failed; memory only.");
                reconciliation?.TrackDelete(key);
                return ok;
            }
        }
        finally
        {
            if (probeTaken)
                Interlocked.Decrement(ref _halfOpenProbeInFlight);
            CacheTelemetry.OpMs.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds, new TagList { { "backend", Name }, { "op", "remove" } });
        }
    }

    public async ValueTask<T?> GetAsync<T>(string key, SpanDeserializer<T> deserialize, CancellationToken ct)
    {
        var bytes = await GetAsync(key, ct).ConfigureAwait(false);
        if (bytes is null) return default;
        return deserialize(bytes);
    }

    public ValueTask SetAsync<T>(string key, T value, Action<IBufferWriter<byte>, T> serialize, CacheEntryOptions options, CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        serialize(buffer, value);
        return SetAsync(key, buffer.WrittenMemory, options, ct);
    }

    public async ValueTask<T> GetOrSetAsync<T>(
        string key,
        Func<CancellationToken, ValueTask<T>> factory,
        Action<IBufferWriter<byte>, T> serialize,
        SpanDeserializer<T> deserialize,
        CacheEntryOptions options,
        CancellationToken ct)
    {
        var bytes = await GetAsync(key, ct).ConfigureAwait(false);
        if (bytes is not null)
            return deserialize(bytes);

        var created = await factory(ct).ConfigureAwait(false);
        await SetAsync(key, created, serialize, options, ct).ConfigureAwait(false);
        return created;
    }

    public void ForceOpen(string reason)
    {
        if (!_breaker.Enabled) return;
        Volatile.Write(ref _forcedReason, reason);
        Volatile.Write(ref _forcedOpen, 1);
        // Ensure state looks "open" even if it was closed previously.
        Volatile.Write(ref _openUntilTicks, 1);
    }

    public void ClearForcedOpen()
    {
        if (!_breaker.Enabled) return;
        Volatile.Write(ref _forcedOpen, 0);
        Volatile.Write(ref _forcedReason, null);
        Volatile.Write(ref _failures, 0);
        Volatile.Write(ref _openUntilTicks, 0);
        Volatile.Write(ref _openAttempts, 0);
        TryStartReconciliation();
    }

    private void TryStartReconciliation()
    {
        if (reconciliation is null)
            return;

        if (Interlocked.CompareExchange(ref _reconcileInFlight, 1, 0) != 0)
            return;

        _ = Task.Run(async () =>
        {
            logger.LogInformation("Starting Redis reconciliation after breaker close.");
            var sw = Stopwatch.StartNew();
            try
            {
                await reconciliation.ReconcileAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Redis reconciliation failed.");
            }
            finally
            {
                logger.LogInformation("Redis reconciliation finished in {Duration}ms.", sw.Elapsed.TotalMilliseconds);
                Interlocked.Exchange(ref _reconcileInFlight, 0);
            }
        });
    }
}
