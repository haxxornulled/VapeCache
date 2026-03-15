using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Core.Policies;

namespace VapeCache.Infrastructure.Caching;

/// <summary>
/// Hybrid cache implementation that combines Redis for distributed caching with
/// automatic failover to in-memory caching when Redis is unavailable.
/// Uses a circuit breaker pattern to detect failures and gradually recover.
/// </summary>
internal sealed partial class HybridCacheService(
    RedisCacheService redis,
    ICacheFallbackService fallback,
    ICurrentCacheService current,
    TimeProvider timeProvider,
    IOptionsMonitor<RedisCircuitBreakerOptions> breakerOptions,
    CacheStatsRegistry statsRegistry,
    ILogger<HybridCacheService> logger,
    IOptionsMonitor<HybridFailoverOptions>? failoverOptions = null,
    IRedisReconciliationService? reconciliation = null,
    IEnterpriseFeatureGate? enterpriseFeatureGate = null) : ICacheService
    , ICacheTagService
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
    private readonly IRedisReconciliationService? _reconciliation =
        enterpriseFeatureGate?.IsReconciliationLicensed == true ? reconciliation : null;
    private static readonly JsonSerializerOptions TagJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly byte[] TagEnvelopePrefix = "VCTAG1:"u8.ToArray();
    private static readonly string[] ReadWarmTags = ["hybrid-failover", "read-warm"];
    private static readonly string[] WriteMirrorTags = ["hybrid-failover", "write-mirror"];
    private const string TagVersionKeyPrefix = "vapecache:tag:v1:";
    private static readonly Action<ILogger, string, Exception?> LogDiscardedStaleTaggedCacheEntry = LoggerMessage.Define<string>(
        LogLevel.Debug,
        new EventId(7001, nameof(LogDiscardedStaleTaggedCacheEntry)),
        "Discarded stale tagged cache entry for key {Key}.");
    private static readonly Action<ILogger, string, Exception?> LogTagVersionReadFallback = LoggerMessage.Define<string>(
        LogLevel.Debug,
        new EventId(7002, nameof(LogTagVersionReadFallback)),
        "Tag version read failed for tag {Tag}; falling back to local version.");
    private static readonly Action<ILogger, string, Exception?> LogRedisTagVersionWriteQueued = LoggerMessage.Define<string>(
        LogLevel.Debug,
        new EventId(7003, nameof(LogRedisTagVersionWriteQueued)),
        "Redis tag version write failed for tag {Tag}; queuing reconciliation write.");
    private static readonly Action<ILogger, string, Exception?> LogFallbackTagVersionWarmFailed = LoggerMessage.Define<string>(
        LogLevel.Debug,
        new EventId(7004, nameof(LogFallbackTagVersionWarmFailed)),
        "Fallback tag version warm failed for key {TagVersionKey}.");
    private static readonly Action<ILogger, string, Exception?> LogFallbackReadWarmFailed = LoggerMessage.Define<string>(
        LogLevel.Debug,
        new EventId(7005, nameof(LogFallbackReadWarmFailed)),
        "Fallback read-warm failed for key {Key}.");
    private static readonly Action<ILogger, string, Exception?> LogFallbackWriteMirrorFailed = LoggerMessage.Define<string>(
        LogLevel.Debug,
        new EventId(7006, nameof(LogFallbackWriteMirrorFailed)),
        "Fallback write-mirror failed for key {Key}.");

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
                LogBreakerMaxRetriesReached(logger, attempts);
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
                LogBreakerOpened(logger, failures, fallback.Name, breakDuration.TotalSeconds);
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
                bytes = await ResolveTaggedPayloadAsync(key, bytes, ct).ConfigureAwait(false);
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
                    var fallbackProbeBytes = await fallback.GetAsync(key, ct).ConfigureAwait(false);
                    return await ResolveTaggedPayloadAsync(key, fallbackProbeBytes, ct).ConfigureAwait(false);
                }

                using var probeCts = CreateProbeCts(breaker, probeTaken, ct);

                var v = await redis.GetAsync(key, probeCts?.Token ?? ct).ConfigureAwait(false);
                v = await ResolveTaggedPayloadAsync(key, v, ct).ConfigureAwait(false);
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
                LogRedisGetFallback(logger, ex, fallback.Name);
            }

            var fallbackBytes = await fallback.GetAsync(key, ct).ConfigureAwait(false);
            fallbackBytes = await ResolveTaggedPayloadAsync(key, fallbackBytes, ct).ConfigureAwait(false);
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
        var valueToStore = await WrapPayloadWithTagVersionsAsync(value, options, ct).ConfigureAwait(false);

        var probeTaken = false;
        try
        {
            if (breaker.Enabled && !IsRedisAllowedNow())
            {
                current.SetCurrent(fallback.Name);
                _stats.IncFallbackToMemory();
                CacheTelemetry.FallbackToMemory.Add(1, new TagList { { "backend", Name }, { "reason", "breaker_open" } });
                await fallback.SetAsync(key, valueToStore, options, ct).ConfigureAwait(false);

                // Track write for reconciliation when Redis recovers
                _reconciliation?.TrackWrite(key, valueToStore, options.Ttl);
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
                    await fallback.SetAsync(key, valueToStore, options, ct).ConfigureAwait(false);
                    _reconciliation?.TrackWrite(key, valueToStore, options.Ttl);
                    return;
                }

                using var probeCts = CreateProbeCts(breaker, probeTaken, ct);

                await redis.SetAsync(key, valueToStore, options, probeCts?.Token ?? ct).ConfigureAwait(false);
                MarkRedisSuccess();
                current.SetCurrent(redis.Name);
                await TryMirrorFallbackWriteAsync(key, valueToStore, options, ct).ConfigureAwait(false);
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
                LogRedisSetFallback(logger, ex, fallback.Name);
                await fallback.SetAsync(key, valueToStore, options, ct).ConfigureAwait(false);
                _reconciliation?.TrackWrite(key, valueToStore, options.Ttl);
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
                _reconciliation?.TrackDelete(key);
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
                    _reconciliation?.TrackDelete(key);
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
                LogRedisDeleteFallback(logger, ex, fallback.Name);
                _reconciliation?.TrackDelete(key);
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
                fallbackBytes = await ResolveTaggedPayloadAsync(key, fallbackBytes, ct).ConfigureAwait(false);
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
                    fallbackBytes = await ResolveTaggedPayloadAsync(key, fallbackBytes, ct).ConfigureAwait(false);
                    if (fallbackBytes is null) { _stats.IncMiss(); CacheTelemetry.Misses.Add(1, new TagList { { "backend", Name } }); }
                    else { _stats.IncHit(); CacheTelemetry.Hits.Add(1, new TagList { { "backend", Name } }); }
                    return fallbackBytes is null ? default : deserialize(fallbackBytes);
                }

                using var probeCts = CreateProbeCts(breaker, probeTaken, ct);

                var redisBytes = await redis.GetAsync(key, probeCts?.Token ?? ct).ConfigureAwait(false);
                redisBytes = await ResolveTaggedPayloadAsync(key, redisBytes, ct).ConfigureAwait(false);
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
                LogRedisGetFallback(logger, ex, fallback.Name);
            }

            var bytes = await fallback.GetAsync(key, ct).ConfigureAwait(false);
            bytes = await ResolveTaggedPayloadAsync(key, bytes, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long> InvalidateTagAsync(string tag, CancellationToken ct = default)
    {
        var normalizedTag = CacheTagPolicy.NormalizeTag(tag);
        var currentVersion = await GetCurrentTagVersionAsync(normalizedTag, ct).ConfigureAwait(false);
        var nextVersion = currentVersion == long.MaxValue ? 1 : currentVersion + 1;
        await WriteTagVersionAsync(normalizedTag, nextVersion, ct).ConfigureAwait(false);
        return nextVersion;
    }

    /// <summary>
    /// Gets value.
    /// </summary>
    public ValueTask<long> GetTagVersionAsync(string tag, CancellationToken ct = default)
        => GetCurrentTagVersionAsync(CacheTagPolicy.NormalizeTag(tag), ct);

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask<long> InvalidateZoneAsync(string zone, CancellationToken ct = default)
        => InvalidateTagAsync(CacheTagConventions.ToZoneTag(zone), ct);

    /// <summary>
    /// Gets value.
    /// </summary>
    public ValueTask<long> GetZoneVersionAsync(string zone, CancellationToken ct = default)
        => GetTagVersionAsync(CacheTagConventions.ToZoneTag(zone), ct);

    private async ValueTask<ReadOnlyMemory<byte>> WrapPayloadWithTagVersionsAsync(
        ReadOnlyMemory<byte> payload,
        CacheEntryOptions options,
        CancellationToken ct)
    {
        var tags = CacheTagPolicy.NormalizeTags(existingTags: null, additionalTags: options.Intent?.Tags);
        if (tags.Length == 0)
            return payload;

        var tagVersions = await CaptureTagVersionsAsync(tags, ct).ConfigureAwait(false);
        return SerializeTagEnvelope(payload, tagVersions);
    }

    private async ValueTask<Dictionary<string, long>> CaptureTagVersionsAsync(string[] normalizedTags, CancellationToken ct)
    {
        var result = new Dictionary<string, long>(normalizedTags.Length, StringComparer.Ordinal);
        foreach (var tag in normalizedTags)
            result[tag] = await GetCurrentTagVersionAsync(tag, ct).ConfigureAwait(false);

        return result;
    }

    private async ValueTask<byte[]?> ResolveTaggedPayloadAsync(string key, byte[]? payload, CancellationToken ct)
    {
        if (payload is null)
            return null;

        if (!TryDeserializeTagEnvelope(payload, out var envelope))
            return payload;

        var tagVersions = envelope.TagVersions;
        if (tagVersions is null || tagVersions.Count == 0)
            return envelope.Payload;

        if (await AreTagVersionsCurrentAsync(tagVersions, ct).ConfigureAwait(false))
            return envelope.Payload;

        await TryRemoveTaggedStaleEntryAsync(key, ct).ConfigureAwait(false);
        LogDiscardedStaleTaggedCacheEntry(logger, key, null);
        return null;
    }

    private async ValueTask<bool> AreTagVersionsCurrentAsync(
        IReadOnlyDictionary<string, long> expectedVersions,
        CancellationToken ct)
    {
        foreach (var expected in expectedVersions)
        {
            var currentVersion = await GetCurrentTagVersionAsync(expected.Key, ct).ConfigureAwait(false);
            if (currentVersion != expected.Value)
                return false;
        }

        return true;
    }

    private async ValueTask<long> GetCurrentTagVersionAsync(string normalizedTag, CancellationToken ct)
    {
        var tagVersionKey = BuildTagVersionKey(normalizedTag);
        var fallbackVersion = await GetTagVersionFromBackendAsync(fallback, tagVersionKey, ct).ConfigureAwait(false);

        var breaker = _breaker;
        if (breaker.Enabled && !IsRedisAllowedNow())
            return fallbackVersion;

        var probeTaken = false;
        try
        {
            if (!TryEnterHalfOpenProbe(breaker, out probeTaken))
                return fallbackVersion;

            using var probeCts = CreateProbeCts(breaker, probeTaken, ct);
            var redisVersion = await GetTagVersionFromBackendAsync(redis, tagVersionKey, probeCts?.Token ?? ct).ConfigureAwait(false);
            MarkRedisSuccess();
            if (redisVersion <= fallbackVersion)
                return fallbackVersion;

            await TrySetFallbackTagVersionAsync(tagVersionKey, redisVersion, ct).ConfigureAwait(false);
            return redisVersion;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            MarkRedisFailure();
            LogTagVersionReadFallback(logger, normalizedTag, ex);
            return fallbackVersion;
        }
        finally
        {
            if (probeTaken)
                Interlocked.Decrement(ref _halfOpenProbeInFlight);
        }
    }

    private static async ValueTask<long> GetTagVersionFromBackendAsync(ICacheService backend, string tagVersionKey, CancellationToken ct)
    {
        var payload = await backend.GetAsync(tagVersionKey, ct).ConfigureAwait(false);
        return TryDeserializeTagVersion(payload, out var version) ? version : 0;
    }

    private async ValueTask WriteTagVersionAsync(string normalizedTag, long version, CancellationToken ct)
    {
        var tagVersionKey = BuildTagVersionKey(normalizedTag);
        var payload = SerializeTagVersion(version);
        var writeOptions = new CacheEntryOptions(
            Intent: new CacheIntent(
                CacheIntentKind.ComputedView,
                Reason: "cache-tag-version",
                Owner: Name,
                Tags: [normalizedTag]));

        await fallback.SetAsync(tagVersionKey, payload, writeOptions, ct).ConfigureAwait(false);

        var breaker = _breaker;
        if (breaker.Enabled && !IsRedisAllowedNow())
        {
            _reconciliation?.TrackWrite(tagVersionKey, payload, expiry: null);
            return;
        }

        var probeTaken = false;
        try
        {
            if (!TryEnterHalfOpenProbe(breaker, out probeTaken))
            {
                _reconciliation?.TrackWrite(tagVersionKey, payload, expiry: null);
                return;
            }

            using var probeCts = CreateProbeCts(breaker, probeTaken, ct);
            await redis.SetAsync(tagVersionKey, payload, writeOptions, probeCts?.Token ?? ct).ConfigureAwait(false);
            MarkRedisSuccess();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            MarkRedisFailure();
            LogRedisTagVersionWriteQueued(logger, normalizedTag, ex);
            _reconciliation?.TrackWrite(tagVersionKey, payload, expiry: null);
        }
        finally
        {
            if (probeTaken)
                Interlocked.Decrement(ref _halfOpenProbeInFlight);
        }
    }

    private async ValueTask TrySetFallbackTagVersionAsync(string tagVersionKey, long version, CancellationToken ct)
    {
        var payload = SerializeTagVersion(version);
        var writeOptions = new CacheEntryOptions(
            Intent: new CacheIntent(
                CacheIntentKind.ComputedView,
                Reason: "cache-tag-version-fallback-warm",
                Owner: Name));
        try
        {
            await fallback.SetAsync(tagVersionKey, payload, writeOptions, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFallbackTagVersionWarmFailed(logger, tagVersionKey, ex);
        }
    }

    private async ValueTask TryRemoveTaggedStaleEntryAsync(string key, CancellationToken ct)
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
            LogFallbackStaleTagCleanupFailed(logger, ex, key);
        }

        var breaker = _breaker;
        if (breaker.Enabled && !IsRedisAllowedNow())
        {
            _reconciliation?.TrackDelete(key);
            return;
        }

        var probeTaken = false;
        try
        {
            if (!TryEnterHalfOpenProbe(breaker, out probeTaken))
            {
                _reconciliation?.TrackDelete(key);
                return;
            }

            using var probeCts = CreateProbeCts(breaker, probeTaken, ct);
            await redis.RemoveAsync(key, probeCts?.Token ?? ct).ConfigureAwait(false);
            MarkRedisSuccess();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            MarkRedisFailure();
            LogRedisStaleTagCleanupQueued(logger, ex, key);
            _reconciliation?.TrackDelete(key);
        }
        finally
        {
            if (probeTaken)
                Interlocked.Decrement(ref _halfOpenProbeInFlight);
        }
    }

    private static string BuildTagVersionKey(string normalizedTag)
        => $"{TagVersionKeyPrefix}{normalizedTag}";

    private static byte[] SerializeTagVersion(long version)
    {
        var bytes = GC.AllocateUninitializedArray<byte>(sizeof(long));
        BinaryPrimitives.WriteInt64LittleEndian(bytes, version);
        return bytes;
    }

    private static bool TryDeserializeTagVersion(byte[]? payload, out long version)
    {
        version = 0;
        if (payload is null || payload.Length != sizeof(long))
            return false;

        version = BinaryPrimitives.ReadInt64LittleEndian(payload);
        return true;
    }

    private static byte[] SerializeTagEnvelope(ReadOnlyMemory<byte> payload, Dictionary<string, long> tagVersions)
    {
        var envelope = new TaggedCacheEnvelope
        {
            Payload = payload.ToArray(),
            TagVersions = tagVersions
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(envelope, TagJsonOptions);
        var buffer = GC.AllocateUninitializedArray<byte>(TagEnvelopePrefix.Length + json.Length);
        TagEnvelopePrefix.AsSpan().CopyTo(buffer);
        json.AsSpan().CopyTo(buffer.AsSpan(TagEnvelopePrefix.Length));
        return buffer;
    }

    private static bool TryDeserializeTagEnvelope(byte[] payload, out TaggedCacheEnvelope envelope)
    {
        envelope = default!;
        if (!payload.AsSpan().StartsWith(TagEnvelopePrefix))
            return false;

        try
        {
            var parsed = JsonSerializer.Deserialize<TaggedCacheEnvelope>(
                payload.AsSpan(TagEnvelopePrefix.Length),
                TagJsonOptions);
            if (parsed is null || parsed.Payload is null)
                return false;

            parsed.TagVersions ??= new Dictionary<string, long>(StringComparer.Ordinal);
            envelope = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
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
                    Tags: ReadWarmTags));
            await fallback.SetAsync(key, value, writeOptions, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFallbackReadWarmFailed(logger, key, ex);
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
                    Tags: WriteMirrorTags));
            await fallback.SetAsync(key, value, writeOptions, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFallbackWriteMirrorFailed(logger, key, ex);
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
            LogFallbackRemoveFailed(logger, ex, key);
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
            LogReconciliationStart(logger);
            var sw = Stopwatch.StartNew();
            try
            {
                await reconciliation.ReconcileAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogReconciliationFailed(logger, ex);
            }
            finally
            {
                LogReconciliationFinished(logger, sw.Elapsed.TotalMilliseconds);
                Interlocked.Exchange(ref _reconcileInFlight, 0);
            }
        });
    }

    [LoggerMessage(
        EventId = 7007,
        Level = LogLevel.Warning,
        Message = "Redis circuit breaker reached MaxConsecutiveRetries ({Attempts}); holding open indefinitely until manual reset.")]
    private static partial void LogBreakerMaxRetriesReached(ILogger logger, int attempts);

    [LoggerMessage(
        EventId = 7008,
        Level = LogLevel.Warning,
        Message = "Circuit breaker opened after {Failures} consecutive failures. Switching to {Fallback} mode for {Duration} seconds.")]
    private static partial void LogBreakerOpened(ILogger logger, int failures, string fallback, double duration);

    [LoggerMessage(
        EventId = 7009,
        Level = LogLevel.Warning,
        Message = "Redis GET failed; falling back to {Fallback}.")]
    private static partial void LogRedisGetFallback(ILogger logger, Exception exception, string fallback);

    [LoggerMessage(
        EventId = 7010,
        Level = LogLevel.Warning,
        Message = "Redis SET failed; writing to {Fallback}.")]
    private static partial void LogRedisSetFallback(ILogger logger, Exception exception, string fallback);

    [LoggerMessage(
        EventId = 7011,
        Level = LogLevel.Warning,
        Message = "Redis DEL failed; using {Fallback} only.")]
    private static partial void LogRedisDeleteFallback(ILogger logger, Exception exception, string fallback);

    [LoggerMessage(
        EventId = 7012,
        Level = LogLevel.Debug,
        Message = "Fallback stale-tag cleanup failed for key {Key}.")]
    private static partial void LogFallbackStaleTagCleanupFailed(ILogger logger, Exception exception, string key);

    [LoggerMessage(
        EventId = 7013,
        Level = LogLevel.Debug,
        Message = "Redis stale-tag cleanup failed for key {Key}; queued for reconciliation.")]
    private static partial void LogRedisStaleTagCleanupQueued(ILogger logger, Exception exception, string key);

    [LoggerMessage(
        EventId = 7014,
        Level = LogLevel.Debug,
        Message = "Fallback remove failed for key {Key}.")]
    private static partial void LogFallbackRemoveFailed(ILogger logger, Exception exception, string key);

    [LoggerMessage(
        EventId = 7015,
        Level = LogLevel.Information,
        Message = "Starting Redis reconciliation after breaker close.")]
    private static partial void LogReconciliationStart(ILogger logger);

    [LoggerMessage(
        EventId = 7016,
        Level = LogLevel.Warning,
        Message = "Redis reconciliation failed.")]
    private static partial void LogReconciliationFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 7017,
        Level = LogLevel.Information,
        Message = "Redis reconciliation finished in {Duration}ms.")]
    private static partial void LogReconciliationFinished(ILogger logger, double duration);

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

    private sealed class TaggedCacheEnvelope
    {
        public byte[] Payload { get; set; } = Array.Empty<byte>();
        public Dictionary<string, long>? TagVersions { get; set; }
    }
}
