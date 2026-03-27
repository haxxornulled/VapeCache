using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
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
internal sealed partial class HybridCacheService : ICacheService
    , ICacheTagService
    , IRedisCircuitBreakerState
    , IRedisFailoverController
{
    private readonly RedisCacheService redis;
    private readonly ICacheFallbackService fallback;
    private readonly ICurrentCacheService current;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<HybridCacheService> logger;

    public HybridCacheService(
        RedisCacheService redis,
        ICacheFallbackService fallback,
        ICurrentCacheService current,
        TimeProvider timeProvider,
        IOptionsMonitor<RedisCircuitBreakerOptions> breakerOptions,
        CacheStatsRegistry statsRegistry,
        ILogger<HybridCacheService> logger,
        IOptionsMonitor<HybridFailoverOptions>? failoverOptions = null,
        IRedisReconciliationService? reconciliation = null,
        IEnterpriseFeatureGate? enterpriseFeatureGate = null)
    {
        this.redis = redis;
        this.fallback = fallback;
        this.current = current;
        this.timeProvider = timeProvider;
        this.logger = logger;
        _stats = statsRegistry.GetOrCreate(CacheStatsNames.Hybrid);
        _breakerOptions = breakerOptions;
        _failoverOptions = failoverOptions ?? DefaultHybridFailoverOptionsMonitor.Instance;
        this._reconciliation =
            enterpriseFeatureGate?.IsReconciliationLicensed == true ? reconciliation : null;
    }

    /// <inheritdoc />
    public string Name => BackendName;

    private readonly CacheStats _stats;
    private readonly IOptionsMonitor<RedisCircuitBreakerOptions> _breakerOptions;
    private readonly IOptionsMonitor<HybridFailoverOptions> _failoverOptions;
    private RedisCircuitBreakerOptions _breaker => _breakerOptions.CurrentValue;
    private HybridFailoverOptions _failover => _failoverOptions.CurrentValue;
    private int _failures;
    private long _openUntilTicks;
    private int _halfOpenProbeInFlight;
    private int _forcedOpen;
    private string? _forcedReason;
    private int _openAttempts;
    private int _reconcileInFlight;
    private readonly IRedisReconciliationService? _reconciliation;
    private const string BackendName = "hybrid";
    private static readonly byte[] BinaryTagEnvelopePrefix = "VCTAG2:"u8.ToArray();
    private static readonly string[] ReadWarmTags = ["hybrid-failover", "read-warm"];
    private static readonly string[] WriteMirrorTags = ["hybrid-failover", "write-mirror"];
    private static readonly CacheIntent ReadWarmIntent = new(
        CacheIntentKind.ReadThrough,
        Reason: "hybrid-failover-read-warm",
        Owner: BackendName,
        Tags: ReadWarmTags);
    private static readonly CacheIntent WriteMirrorIntent = new(
        CacheIntentKind.ReadThrough,
        Reason: "hybrid-failover-write-mirror",
        Owner: BackendName,
        Tags: WriteMirrorTags);
    private static readonly CacheIntent TagVersionFallbackWarmIntent = new(
        CacheIntentKind.ComputedView,
        Reason: "cache-tag-version-fallback-warm",
        Owner: BackendName);
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
                var resolvedRedisPayload = await ResolveTaggedPayloadCoreAsync(key, v, ct).ConfigureAwait(false);
                MarkRedisSuccess();
                current.SetCurrent(redis.Name);
                if (resolvedRedisPayload.HasValue)
                {
                    await TryWarmFallbackFromReadAsync(key, resolvedRedisPayload.Payload, ct).ConfigureAwait(false);
                    _stats.IncHit();
                    CacheTelemetry.Hits.Add(1, new TagList { { "backend", Name } });
                    return resolvedRedisPayload.ToArray();
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
                var resolvedRedisPayload = await ResolveTaggedPayloadCoreAsync(key, redisBytes, ct).ConfigureAwait(false);
                MarkRedisSuccess();
                current.SetCurrent(redis.Name);
                if (resolvedRedisPayload.HasValue)
                {
                    await TryWarmFallbackFromReadAsync(key, resolvedRedisPayload.Payload, ct).ConfigureAwait(false);
                    _stats.IncHit();
                    CacheTelemetry.Hits.Add(1, new TagList { { "backend", Name } });
                    return deserialize(resolvedRedisPayload.Payload.Span);
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

    private async ValueTask<TagVersionSnapshot[]> CaptureTagVersionsAsync(string[] normalizedTags, CancellationToken ct)
    {
        var result = new TagVersionSnapshot[normalizedTags.Length];
        for (var i = 0; i < normalizedTags.Length; i++)
        {
            var tag = normalizedTags[i];
            result[i] = new TagVersionSnapshot(tag, await GetCurrentTagVersionAsync(tag, ct).ConfigureAwait(false));
        }

        return result;
    }

    private async ValueTask<byte[]?> ResolveTaggedPayloadAsync(string key, byte[]? payload, CancellationToken ct)
    {
        var resolved = await ResolveTaggedPayloadCoreAsync(key, payload, ct).ConfigureAwait(false);
        return resolved.HasValue ? resolved.ToArray() : null;
    }

    private async ValueTask<TaggedPayloadResolution> ResolveTaggedPayloadCoreAsync(string key, byte[]? payload, CancellationToken ct)
    {
        if (payload is null)
            return default;

        if (TryDeserializeBinaryTagEnvelope(payload, out var binaryEnvelope))
        {
            if (binaryEnvelope.TagVersions.Length == 0
                || await AreTagVersionsCurrentAsync(binaryEnvelope.TagVersions, ct).ConfigureAwait(false))
            {
                return new TaggedPayloadResolution(binaryEnvelope.Payload);
            }

            await TryRemoveTaggedStaleEntryAsync(key, ct).ConfigureAwait(false);
            LogDiscardedStaleTaggedCacheEntry(logger, key, null);
            return default;
        }

        return new TaggedPayloadResolution(payload, payload);
    }

    private async ValueTask<bool> AreTagVersionsCurrentAsync(
        TagVersionSnapshot[] expectedVersions,
        CancellationToken ct)
    {
        foreach (var expected in expectedVersions)
        {
            var currentVersion = await GetCurrentTagVersionAsync(expected.Tag, ct).ConfigureAwait(false);
            if (currentVersion != expected.Version)
                return false;
        }

        return true;
    }

    private async ValueTask<long> GetCurrentTagVersionAsync(string normalizedTag, CancellationToken ct)
    {
        var tagVersionKey = BuildTagVersionKey(normalizedTag);

        var breaker = _breaker;
        if (breaker.Enabled && !IsRedisAllowedNow())
            return await GetTagVersionFromBackendAsync(fallback, tagVersionKey, ct).ConfigureAwait(false);

        var probeTaken = false;
        try
        {
            if (!TryEnterHalfOpenProbe(breaker, out probeTaken))
                return await GetTagVersionFromBackendAsync(fallback, tagVersionKey, ct).ConfigureAwait(false);

            using var probeCts = CreateProbeCts(breaker, probeTaken, ct);
            if (ShouldPreferRedisFirstTagVersionRead())
            {
                var redisVersion = await GetTagVersionFromBackendAsync(redis, tagVersionKey, probeCts?.Token ?? ct).ConfigureAwait(false);
                MarkRedisSuccess();
                if (redisVersion == 0)
                    return 0;

                var fallbackVersion = await GetTagVersionFromBackendAsync(fallback, tagVersionKey, ct).ConfigureAwait(false);
                if (fallbackVersion > redisVersion)
                    return fallbackVersion;

                if (redisVersion > fallbackVersion)
                    await TrySetFallbackTagVersionAsync(tagVersionKey, redisVersion, ct).ConfigureAwait(false);

                return redisVersion;
            }

            var currentFallbackVersion = await GetTagVersionFromBackendAsync(fallback, tagVersionKey, ct).ConfigureAwait(false);
            var currentRedisVersion = await GetTagVersionFromBackendAsync(redis, tagVersionKey, probeCts?.Token ?? ct).ConfigureAwait(false);
            MarkRedisSuccess();
            if (currentRedisVersion <= currentFallbackVersion)
                return currentFallbackVersion;

            await TrySetFallbackTagVersionAsync(tagVersionKey, currentRedisVersion, ct).ConfigureAwait(false);
            return currentRedisVersion;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            MarkRedisFailure();
            LogTagVersionReadFallback(logger, normalizedTag, ex);
            return await GetTagVersionFromBackendAsync(fallback, tagVersionKey, ct).ConfigureAwait(false);
        }
        finally
        {
            if (probeTaken)
                Interlocked.Decrement(ref _halfOpenProbeInFlight);
        }
    }

    private bool ShouldPreferRedisFirstTagVersionRead()
        => _reconciliation is null || _reconciliation.PendingOperations <= 0;

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
        var writeOptions = new CacheEntryOptions(Intent: TagVersionFallbackWarmIntent);
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

    private static byte[] SerializeTagEnvelope(ReadOnlyMemory<byte> payload, TagVersionSnapshot[] tagVersions)
    {
        var length = BinaryTagEnvelopePrefix.Length + sizeof(int) + sizeof(int) + payload.Length;
        for (var i = 0; i < tagVersions.Length; i++)
        {
            var tag = tagVersions[i];
            length += sizeof(int) + Encoding.UTF8.GetByteCount(tag.Tag) + sizeof(long);
        }

        var buffer = GC.AllocateUninitializedArray<byte>(length);
        var span = buffer.AsSpan();
        var offset = 0;

        BinaryTagEnvelopePrefix.CopyTo(buffer, offset);
        offset += BinaryTagEnvelopePrefix.Length;

        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, sizeof(int)), tagVersions.Length);
        offset += sizeof(int);

        for (var i = 0; i < tagVersions.Length; i++)
        {
            var tag = tagVersions[i];
            var tagLength = Encoding.UTF8.GetByteCount(tag.Tag);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, sizeof(int)), tagLength);
            offset += sizeof(int);
            offset += Encoding.UTF8.GetBytes(tag.Tag, span.Slice(offset, tagLength));
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset, sizeof(long)), tag.Version);
            offset += sizeof(long);
        }

        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, sizeof(int)), payload.Length);
        offset += sizeof(int);
        payload.Span.CopyTo(span.Slice(offset, payload.Length));
        return buffer;
    }

    private static bool TryDeserializeBinaryTagEnvelope(byte[] payload, out BinaryTaggedCacheEnvelope envelope)
    {
        envelope = default;
        var span = payload.AsSpan();
        if (!span.StartsWith(BinaryTagEnvelopePrefix))
            return false;

        var offset = BinaryTagEnvelopePrefix.Length;
        if (!TryReadInt32(span, ref offset, out var tagCount)
            || tagCount < 0
            || tagCount > 1024)
        {
            return false;
        }

        var tagVersions = tagCount == 0 ? Array.Empty<TagVersionSnapshot>() : new TagVersionSnapshot[tagCount];
        for (var i = 0; i < tagCount; i++)
        {
            if (!TryReadInt32(span, ref offset, out var tagByteCount)
                || tagByteCount < 0
                || span.Length - offset < tagByteCount + sizeof(long))
            {
                return false;
            }

            var tag = Encoding.UTF8.GetString(span.Slice(offset, tagByteCount));
            offset += tagByteCount;
            if (!TryReadInt64(span, ref offset, out var version))
                return false;

            tagVersions[i] = new TagVersionSnapshot(tag, version);
        }

        if (!TryReadInt32(span, ref offset, out var payloadLength)
            || payloadLength < 0
            || span.Length - offset != payloadLength)
        {
            return false;
        }

        envelope = new BinaryTaggedCacheEnvelope(payload, offset, payloadLength, tagVersions);
        return true;
    }

    private async ValueTask TryWarmFallbackFromReadAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        var options = _failover;
        if (!options.WarmFallbackOnRedisReadHit || !ShouldMirrorPayload(options, value.Length))
            return;

        try
        {
            var writeOptions = new CacheEntryOptions(Ttl: options.FallbackWarmReadTtl, Intent: ReadWarmIntent);
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
                Intent: sourceOptions.Intent ?? WriteMirrorIntent);
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

    private static bool TryReadInt32(ReadOnlySpan<byte> buffer, ref int offset, out int value)
    {
        value = 0;
        if (buffer.Length - offset < sizeof(int))
            return false;

        value = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset, sizeof(int)));
        offset += sizeof(int);
        return true;
    }

    private static bool TryReadInt64(ReadOnlySpan<byte> buffer, ref int offset, out long value)
    {
        value = 0;
        if (buffer.Length - offset < sizeof(long))
            return false;

        value = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset, sizeof(long)));
        offset += sizeof(long);
        return true;
    }

    private static byte[] CopyBuffer(ReadOnlySpan<byte> value)
    {
        var buffer = GC.AllocateUninitializedArray<byte>(value.Length);
        value.CopyTo(buffer);
        return buffer;
    }

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
        if (_reconciliation is null)
            return;

        if (Interlocked.CompareExchange(ref _reconcileInFlight, 1, 0) != 0)
            return;

        _ = Task.Run(async () =>
        {
            LogReconciliationStart(logger);
            var sw = Stopwatch.StartNew();
            try
            {
                await _reconciliation.ReconcileAsync().ConfigureAwait(false);
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

    private readonly struct TagVersionSnapshot
    {
        public TagVersionSnapshot(string tag, long version)
        {
            Tag = tag;
            Version = version;
        }

        public string Tag { get; }
        public long Version { get; }
    }

    private readonly struct BinaryTaggedCacheEnvelope
    {
        public BinaryTaggedCacheEnvelope(
            byte[] buffer,
            int payloadOffset,
            int payloadLength,
            TagVersionSnapshot[] tagVersions)
        {
            Buffer = buffer;
            PayloadOffset = payloadOffset;
            PayloadLength = payloadLength;
            TagVersions = tagVersions;
        }

        public byte[] Buffer { get; }
        public int PayloadOffset { get; }
        public int PayloadLength { get; }
        public TagVersionSnapshot[] TagVersions { get; }
        public ReadOnlyMemory<byte> Payload => Buffer.AsMemory(PayloadOffset, PayloadLength);
    }

    private readonly struct TaggedPayloadResolution
    {
        public TaggedPayloadResolution(ReadOnlyMemory<byte> payload, byte[]? exactArray = null)
        {
            Payload = payload;
            ExactArray = exactArray;
            HasValue = true;
        }

        public bool HasValue { get; }
        public ReadOnlyMemory<byte> Payload { get; }
        private byte[]? ExactArray { get; }
        public byte[] ToArray() => ExactArray ?? CopyBuffer(Payload.Span);
    }
}
