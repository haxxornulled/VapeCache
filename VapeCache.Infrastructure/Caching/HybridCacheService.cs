using System.Buffers;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;

namespace VapeCache.Infrastructure.Caching;

internal sealed class HybridCacheService(
    RedisCacheService redis,
    InMemoryCacheService memory,
    ICurrentCacheService current,
    TimeProvider timeProvider,
    IOptions<RedisCircuitBreakerOptions> breakerOptions,
    CacheStats stats,
    ILogger<HybridCacheService> logger,
    IRedisReconciliationService? reconciliation = null) : ICacheService
    , IRedisCircuitBreakerState
    , IRedisFailoverController
{
    public string Name => "hybrid";

    private readonly RedisCircuitBreakerOptions _breaker = breakerOptions.Value;
    private int _failures;
    private long _openUntilTicks;
    private int _halfOpenProbeInFlight;
    private int _forcedOpen;
    private string? _forcedReason;

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
        Volatile.Write(ref _failures, 0);
        Volatile.Write(ref _openUntilTicks, 0);
    }

    public void MarkRedisFailure()
    {
        if (!_breaker.Enabled) return;
        var failures = Interlocked.Increment(ref _failures);
        if (failures >= Math.Max(1, _breaker.ConsecutiveFailuresToOpen))
        {
            var until = AddDurationToTimestamp(timeProvider.GetTimestamp(), _breaker.BreakDuration);
            Volatile.Write(ref _openUntilTicks, until);
            if (failures == _breaker.ConsecutiveFailuresToOpen)
            {
                stats.IncBreakerOpened();
                CacheTelemetry.RedisBreakerOpened.Add(1, new TagList { { "backend", Name } });
                logger.LogWarning("⚡ CIRCUIT BREAKER OPENED after {Failures} consecutive failures. Switching to in-memory mode for {Duration} seconds.", failures, _breaker.BreakDuration.TotalSeconds);
                System.Console.WriteLine($"\n🔥🔥🔥 ⚡ CIRCUIT BREAKER OPENED! Failures={failures}, switching to IN-MEMORY mode for {_breaker.BreakDuration.TotalSeconds} seconds 🔥🔥🔥\n");
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
        stats.IncGet();
        CacheTelemetry.GetCalls.Add(1, new TagList { { "backend", Name } });
        var start = Stopwatch.GetTimestamp();

        var probeTaken = false;
        try
        {
            if (_breaker.Enabled && !IsRedisAllowedNow())
            {
                current.SetCurrent(memory.Name);
                stats.IncFallbackToMemory();
                CacheTelemetry.FallbackToMemory.Add(1, new TagList { { "backend", Name }, { "reason", "breaker_open" } });

                var bytes = await memory.GetAsync(key, ct).ConfigureAwait(false);
                if (bytes is null) { stats.IncMiss(); CacheTelemetry.Misses.Add(1, new TagList { { "backend", Name } }); }
                else { stats.IncHit(); CacheTelemetry.Hits.Add(1, new TagList { { "backend", Name } }); }
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
                        stats.IncFallbackToMemory();
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
                    stats.IncHit();
                    CacheTelemetry.Hits.Add(1, new TagList { { "backend", Name } });
                    return v;
                }
            }
            catch (Exception ex)
            {
                MarkRedisFailure();
                current.SetCurrent(memory.Name);
                stats.IncFallbackToMemory();
                CacheTelemetry.FallbackToMemory.Add(1, new TagList { { "backend", Name }, { "reason", "redis_error" } });
                logger.LogWarning(ex, "Redis GET failed; falling back to memory.");
            }

            var fallbackBytes = await memory.GetAsync(key, ct).ConfigureAwait(false);
            if (fallbackBytes is null) { stats.IncMiss(); CacheTelemetry.Misses.Add(1, new TagList { { "backend", Name } }); }
            else { stats.IncHit(); CacheTelemetry.Hits.Add(1, new TagList { { "backend", Name } }); }
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
        stats.IncSet();
        CacheTelemetry.SetCalls.Add(1, new TagList { { "backend", Name } });
        var start = Stopwatch.GetTimestamp();

        var probeTaken = false;
        try
        {
            if (_breaker.Enabled && !IsRedisAllowedNow())
            {
                current.SetCurrent(memory.Name);
                stats.IncFallbackToMemory();
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
                        stats.IncFallbackToMemory();
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
                stats.IncFallbackToMemory();
                CacheTelemetry.FallbackToMemory.Add(1, new TagList { { "backend", Name }, { "reason", "redis_error" } });
                logger.LogWarning(ex, "Redis SET failed; writing to memory.");
                await memory.SetAsync(key, value, options, ct).ConfigureAwait(false);
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
        stats.IncRemove();
        CacheTelemetry.RemoveCalls.Add(1, new TagList { { "backend", Name } });
        var start = Stopwatch.GetTimestamp();
        var ok = await memory.RemoveAsync(key, ct).ConfigureAwait(false);
        var probeTaken = false;
        try
        {
            if (_breaker.Enabled && !IsRedisAllowedNow())
            {
                current.SetCurrent(memory.Name);
                stats.IncFallbackToMemory();
                CacheTelemetry.FallbackToMemory.Add(1, new TagList { { "backend", Name }, { "reason", "breaker_open" } });
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
                        stats.IncFallbackToMemory();
                        CacheTelemetry.FallbackToMemory.Add(1, new TagList { { "backend", Name }, { "reason", "half_open_busy" } });
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
                stats.IncFallbackToMemory();
                CacheTelemetry.FallbackToMemory.Add(1, new TagList { { "backend", Name }, { "reason", "redis_error" } });
                logger.LogWarning(ex, "Redis DEL failed; memory only.");
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
    }
}
