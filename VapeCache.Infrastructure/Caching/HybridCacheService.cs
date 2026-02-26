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
    ICacheFallbackService fallback,
    ICurrentCacheService current,
    TimeProvider timeProvider,
    IOptionsMonitor<RedisCircuitBreakerOptions> breakerOptions,
    CacheStatsRegistry statsRegistry,
    ILogger<HybridCacheService> logger,
    IOptionsMonitor<HybridFailoverOptions>? failoverOptions = null,
    IRedisReconciliationService? reconciliation = null) : ICacheService
    , IRedisCircuitBreakerState
    , IRedisFailoverController
{
    /// <inheritdoc />
    public string Name => "hybrid";

    private readonly CacheStats _stats = statsRegistry.GetOrCreate(CacheStatsNames.Hybrid);
    private readonly IOptionsMonitor<RedisCircuitBreakerOptions> _breakerOptions = breakerOptions;
    private readonly IOptionsMonitor<HybridFailoverOptions> _failoverOptions = failoverOptions ?? DefaultHybridFailoverOptionsMonitor.Instance;
    private RedisCircuitBreakerOptions _breaker => _breakerOptions.CurrentValue;
    private HybridFailoverOptions _failover => _failoverOptions.CurrentValue;
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

    private bool TryEnterHalfOpenProbe(in RedisCircuitBreakerOptions breaker, out bool probeTaken)
    {
        probeTaken = false;
        if (!breaker.Enabled || Volatile.Read(ref _openUntilTicks) == 0)
            return true;

        var probeNum = Interlocked.Increment(ref _halfOpenProbeInFlight);
        probeTaken = true;
        if (probeNum <= breaker.MaxHalfOpenProbes)
            return true;

        Interlocked.Decrement(ref _halfOpenProbeInFlight);
        probeTaken = false;
        return false;
    }

    private static CancellationTokenSource? CreateProbeCts(in RedisCircuitBreakerOptions breaker, bool probeTaken, CancellationToken ct)
    {
        if (!probeTaken || breaker.HalfOpenProbeTimeout <= TimeSpan.Zero)
            return null;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(breaker.HalfOpenProbeTimeout);
        return cts;
    }

    /// <summary>
    /// Executes value.
    /// </summary>
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

    /// <summary>
    /// Executes value.
    /// </summary>
    public void MarkRedisFailure()
    {
        if (!_breaker.Enabled) return;
        var breaker = _breaker;
        var failures = Interlocked.Increment(ref _failures);
        var threshold = Math.Max(1, breaker.ConsecutiveFailuresToOpen);
        if (failures >= threshold)
        {
            var attempts = Interlocked.Increment(ref _openAttempts);
            var breakDuration = breaker.BreakDuration;
            if (breaker.UseExponentialBackoff)
            {
                var scaled = breaker.BreakDuration.TotalSeconds * Math.Pow(2, Math.Max(0, attempts - 1));
                var capped = Math.Min(breaker.MaxBreakDuration.TotalSeconds, scaled);
                breakDuration = TimeSpan.FromSeconds(capped);
            }

            // If we've exceeded max retries, hold the breaker open indefinitely
            var wasClosed = Volatile.Read(ref _openUntilTicks) == 0;
            if (breaker.MaxConsecutiveRetries > 0 && attempts > breaker.MaxConsecutiveRetries)
            {
                Volatile.Write(ref _openUntilTicks, long.MaxValue);
                logger.LogWarning("Redis circuit breaker reached MaxConsecutiveRetries ({Attempts}); holding open indefinitely until manual reset.", attempts);
            }
            else
            {
                var until = AddDurationToTimestamp(timeProvider.GetTimestamp(), breakDuration);
                Volatile.Write(ref _openUntilTicks, until);
            }
            if (wasClosed)
            {
                _stats.IncBreakerOpened();
                CacheTelemetry.RedisBreakerOpened.Add(1, new TagList { { "backend", Name } });
                logger.LogWarning("Circuit breaker opened after {Failures} consecutive failures. Switching to {Fallback} mode for {Duration} seconds.", failures, fallback.Name, breakDuration.TotalSeconds);
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

    /// <summary>
    /// Gets value.
    /// </summary>
    public async ValueTask<byte[]?> GetAsync(string key, CancellationToken ct)
    {
        var breaker = _breaker;
        _stats.IncGet();
        CacheTelemetry.GetCalls.Add(1, new TagList { { "backend", Name } });
        var start = Stopwatch.GetTimestamp();

        var probeTaken = false;
        try
        {
            if (breaker.Enabled && !IsRedisAllowedNow())
            {
                current.SetCurrent(fallback.Name);
                _stats.IncFallbackToMemory();
                CacheTelemetry.FallbackToMemory.Add(1, new TagList { { "backend", Name }, { "reason", "breaker_open" } });

                var bytes = await fallback.GetAsync(key, ct).ConfigureAwait(false);
                if (bytes is null) { _stats.IncMiss(); CacheTelemetry.Misses.Add(1, new TagList { { "backend", Name } }); }
                else { _stats.IncHit(); CacheTelemetry.Hits.Add(1, new TagList { { "backend", Name } }); }
                return bytes;
            }

            try
            {
                // Half-open state: allow limited concurrent Redis probes during recovery
                if (!TryEnterHalfOpenProbe(breaker, out probeTaken))
                {
                    current.SetCurrent(fallback.Name);
                    _stats.IncFallbackToMemory();
                    CacheTelemetry.FallbackToMemory.Add(1, new TagList { { "backend", Name }, { "reason", "half_open_busy" } });
                    return await fallback.GetAsync(key, ct).ConfigureAwait(false);
                }

                using var probeCts = CreateProbeCts(breaker, probeTaken, ct);

                var v = await redis.GetAsync(key, probeCts?.Token ?? ct).ConfigureAwait(false);
                MarkRedisSuccess();
                current.SetCurrent(redis.Name);
                if (v is not null)
                {
                    await TryWarmFallbackFromReadAsync(key, v, ct).ConfigureAwait(false);
                    _stats.IncHit();
                    CacheTelemetry.Hits.Add(1, new TagList { { "backend", Name } });
                    return v;
                }

                await TryRemoveStaleFallbackOnMissAsync(key, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                MarkRedisFailure();
                current.SetCurrent(fallback.Name);
                _stats.IncFallbackToMemory();
                CacheTelemetry.FallbackToMemory.Add(1, new TagList { { "backend", Name }, { "reason", "redis_error" } });
                logger.LogWarning(ex, "Redis GET failed; falling back to {Fallback}.", fallback.Name);
            }

            var fallbackBytes = await fallback.GetAsync(key, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Sets value.
    /// </summary>
    public async ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CacheEntryOptions options, CancellationToken ct)
    {
        var breaker = _breaker;
        _stats.IncSet();
        CacheTelemetry.SetCalls.Add(1, new TagList { { "backend", Name } });
        CacheTelemetry.SetPayloadBytes.Record(value.Length, new TagList
        {
            { "backend", Name },
            { "bucket", CacheTelemetry.GetPayloadBucket(value.Length) }
        });
        if (value.Length > 65536)
            CacheTelemetry.LargeKeyWrites.Add(1, new TagList { { "backend", Name } });
        var start = Stopwatch.GetTimestamp();

        var probeTaken = false;
        try
        {
            if (breaker.Enabled && !IsRedisAllowedNow())
            {
                current.SetCurrent(fallback.Name);
                _stats.IncFallbackToMemory();
                CacheTelemetry.FallbackToMemory.Add(1, new TagList { { "backend", Name }, { "reason", "breaker_open" } });
                await fallback.SetAsync(key, value, options, ct).ConfigureAwait(false);

                // Track write for reconciliation when Redis recovers
                reconciliation?.TrackWrite(key, value, options.Ttl);
                return;
            }

            try
            {
                // Half-open state: allow limited concurrent Redis probes during recovery
                if (!TryEnterHalfOpenProbe(breaker, out probeTaken))
                {
                    current.SetCurrent(fallback.Name);
                    _stats.IncFallbackToMemory();
                    CacheTelemetry.FallbackToMemory.Add(1, new TagList { { "backend", Name }, { "reason", "half_open_busy" } });
                    await fallback.SetAsync(key, value, options, ct).ConfigureAwait(false);
                    reconciliation?.TrackWrite(key, value, options.Ttl);
                    return;
                }

                using var probeCts = CreateProbeCts(breaker, probeTaken, ct);

                await redis.SetAsync(key, value, options, probeCts?.Token ?? ct).ConfigureAwait(false);
                MarkRedisSuccess();
                current.SetCurrent(redis.Name);
                await TryMirrorFallbackWriteAsync(key, value, options, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                MarkRedisFailure();
                current.SetCurrent(fallback.Name);
                _stats.IncFallbackToMemory();
                CacheTelemetry.FallbackToMemory.Add(1, new TagList { { "backend", Name }, { "reason", "redis_error" } });
                logger.LogWarning(ex, "Redis SET failed; writing to {Fallback}.", fallback.Name);
                await fallback.SetAsync(key, value, options, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Removes value.
    /// </summary>
    public async ValueTask<bool> RemoveAsync(string key, CancellationToken ct)
    {
        var breaker = _breaker;
        _stats.IncRemove();
        CacheTelemetry.RemoveCalls.Add(1, new TagList { { "backend", Name } });
        var start = Stopwatch.GetTimestamp();
        var ok = await fallback.RemoveAsync(key, ct).ConfigureAwait(false);
        var probeTaken = false;
        try
        {
            if (breaker.Enabled && !IsRedisAllowedNow())
            {
                current.SetCurrent(fallback.Name);
                _stats.IncFallbackToMemory();
                CacheTelemetry.FallbackToMemory.Add(1, new TagList { { "backend", Name }, { "reason", "breaker_open" } });
                reconciliation?.TrackDelete(key);
                return ok;
            }

            try
            {
                // Half-open state: allow limited concurrent Redis probes during recovery
                if (!TryEnterHalfOpenProbe(breaker, out probeTaken))
                {
                    current.SetCurrent(fallback.Name);
                    _stats.IncFallbackToMemory();
                    CacheTelemetry.FallbackToMemory.Add(1, new TagList { { "backend", Name }, { "reason", "half_open_busy" } });
                    reconciliation?.TrackDelete(key);
                    return ok;
                }

                using var probeCts = CreateProbeCts(breaker, probeTaken, ct);

                var rok = await redis.RemoveAsync(key, probeCts?.Token ?? ct).ConfigureAwait(false);
                MarkRedisSuccess();
                current.SetCurrent(redis.Name);
                return ok || rok;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                MarkRedisFailure();
                current.SetCurrent(fallback.Name);
                _stats.IncFallbackToMemory();
                CacheTelemetry.FallbackToMemory.Add(1, new TagList { { "backend", Name }, { "reason", "redis_error" } });
                logger.LogWarning(ex, "Redis DEL failed; using {Fallback} only.", fallback.Name);
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
        var breaker = _breaker;
        _stats.IncGet();
        CacheTelemetry.GetCalls.Add(1, new TagList { { "backend", Name } });
        var start = Stopwatch.GetTimestamp();

        var probeTaken = false;
        try
        {
            if (breaker.Enabled && !IsRedisAllowedNow())
            {
                current.SetCurrent(fallback.Name);
                _stats.IncFallbackToMemory();
                CacheTelemetry.FallbackToMemory.Add(1, new TagList { { "backend", Name }, { "reason", "breaker_open" } });

                var fallbackBytes = await fallback.GetAsync(key, ct).ConfigureAwait(false);
                if (fallbackBytes is null) { _stats.IncMiss(); CacheTelemetry.Misses.Add(1, new TagList { { "backend", Name } }); }
                else { _stats.IncHit(); CacheTelemetry.Hits.Add(1, new TagList { { "backend", Name } }); }
                return fallbackBytes is null ? default : deserialize(fallbackBytes);
            }

            try
            {
                if (!TryEnterHalfOpenProbe(breaker, out probeTaken))
                {
                    current.SetCurrent(fallback.Name);
                    _stats.IncFallbackToMemory();
                    CacheTelemetry.FallbackToMemory.Add(1, new TagList { { "backend", Name }, { "reason", "half_open_busy" } });
                    var fallbackBytes = await fallback.GetAsync(key, ct).ConfigureAwait(false);
                    if (fallbackBytes is null) { _stats.IncMiss(); CacheTelemetry.Misses.Add(1, new TagList { { "backend", Name } }); }
                    else { _stats.IncHit(); CacheTelemetry.Hits.Add(1, new TagList { { "backend", Name } }); }
                    return fallbackBytes is null ? default : deserialize(fallbackBytes);
                }

                using var probeCts = CreateProbeCts(breaker, probeTaken, ct);

                var redisBytes = await redis.GetAsync(key, probeCts?.Token ?? ct).ConfigureAwait(false);
                MarkRedisSuccess();
                current.SetCurrent(redis.Name);
                if (redisBytes is not null)
                {
                    await TryWarmFallbackFromReadAsync(key, redisBytes, ct).ConfigureAwait(false);
                    _stats.IncHit();
                    CacheTelemetry.Hits.Add(1, new TagList { { "backend", Name } });
                    return deserialize(redisBytes);
                }

                await TryRemoveStaleFallbackOnMissAsync(key, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                MarkRedisFailure();
                current.SetCurrent(fallback.Name);
                _stats.IncFallbackToMemory();
                CacheTelemetry.FallbackToMemory.Add(1, new TagList { { "backend", Name }, { "reason", "redis_error" } });
                logger.LogWarning(ex, "Redis GET failed; falling back to {Fallback}.", fallback.Name);
            }

            var bytes = await fallback.GetAsync(key, ct).ConfigureAwait(false);
            if (bytes is null) { _stats.IncMiss(); CacheTelemetry.Misses.Add(1, new TagList { { "backend", Name } }); }
            else { _stats.IncHit(); CacheTelemetry.Hits.Add(1, new TagList { { "backend", Name } }); }
            return bytes is null ? default : deserialize(bytes);
        }
        finally
        {
            if (probeTaken)
                Interlocked.Decrement(ref _halfOpenProbeInFlight);
            CacheTelemetry.OpMs.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds, new TagList { { "backend", Name }, { "op", "get" } });
        }
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

    private async ValueTask TryWarmFallbackFromReadAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        var options = _failover;
        if (!options.WarmFallbackOnRedisReadHit || !ShouldMirrorPayload(options, value.Length))
            return;

        try
        {
            var writeOptions = new CacheEntryOptions(
                Ttl: options.FallbackWarmReadTtl,
                Intent: new CacheIntent(
                    CacheIntentKind.ReadThrough,
                    Reason: "hybrid-failover-read-warm",
                    Owner: Name,
                    Tags: new[] { "hybrid-failover", "read-warm" }));
            await fallback.SetAsync(key, value, writeOptions, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Fallback read-warm failed for key {Key}.", key);
        }
    }

    private async ValueTask TryMirrorFallbackWriteAsync(string key, ReadOnlyMemory<byte> value, CacheEntryOptions sourceOptions, CancellationToken ct)
    {
        var options = _failover;
        if (!options.MirrorWritesToFallbackWhenRedisHealthy)
        {
            await TryRemoveFallbackEntryAsync(key, ct).ConfigureAwait(false);
            return;
        }

        if (!ShouldMirrorPayload(options, value.Length))
        {
            await TryRemoveFallbackEntryAsync(key, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            var ttl = sourceOptions.Ttl ?? options.FallbackMirrorWriteTtlWhenMissing;
            var writeOptions = new CacheEntryOptions(
                Ttl: ttl,
                Intent: sourceOptions.Intent ?? new CacheIntent(
                    CacheIntentKind.ReadThrough,
                    Reason: "hybrid-failover-write-mirror",
                    Owner: Name,
                    Tags: new[] { "hybrid-failover", "write-mirror" }));
            await fallback.SetAsync(key, value, writeOptions, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Fallback write-mirror failed for key {Key}.", key);
        }
    }

    private async ValueTask TryRemoveStaleFallbackOnMissAsync(string key, CancellationToken ct)
    {
        if (!_failover.RemoveStaleFallbackOnRedisMiss)
            return;

        await TryRemoveFallbackEntryAsync(key, ct).ConfigureAwait(false);
    }

    private async ValueTask TryRemoveFallbackEntryAsync(string key, CancellationToken ct)
    {
        try
        {
            await fallback.RemoveAsync(key, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Fallback remove failed for key {Key}.", key);
        }
    }

    private static bool ShouldMirrorPayload(HybridFailoverOptions options, int payloadBytes)
        => options.MaxMirrorPayloadBytes <= 0 || payloadBytes <= options.MaxMirrorPayloadBytes;

    /// <summary>
    /// Executes value.
    /// </summary>
    public void ForceOpen(string reason)
    {
        if (!_breaker.Enabled) return;
        Volatile.Write(ref _forcedReason, reason);
        Volatile.Write(ref _forcedOpen, 1);
        // Ensure state looks "open" even if it was closed previously.
        Volatile.Write(ref _openUntilTicks, 1);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
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

    private sealed class DefaultHybridFailoverOptionsMonitor : IOptionsMonitor<HybridFailoverOptions>
    {
        public static readonly DefaultHybridFailoverOptionsMonitor Instance = new();
        private static readonly HybridFailoverOptions ValueInstance = new();

        public HybridFailoverOptions CurrentValue => ValueInstance;
        public HybridFailoverOptions Get(string? name) => ValueInstance;
        public IDisposable OnChange(Action<HybridFailoverOptions, string?> listener) => NoopDisposable.Instance;

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();
            public void Dispose()
            {
            }
        }
    }
}
