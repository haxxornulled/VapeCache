using System.Diagnostics;
using System.Diagnostics.Metrics;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Diagnostics;

namespace VapeCache.Infrastructure.Caching;

/// <summary>
/// Represents the cache telemetry.
/// </summary>
public static class CacheTelemetry
{
    /// <summary>
    /// Executes new.
    /// </summary>
    public static readonly Meter Meter = new("VapeCache.Cache");

    /// <summary>
    /// Defines the get calls.
    /// </summary>
    public static readonly Counter<long> GetCalls = Meter.CreateCounter<long>("cache.get.calls", description: "Total GET operations");
    /// <summary>
    /// Defines the hits.
    /// </summary>
    public static readonly Counter<long> Hits = Meter.CreateCounter<long>("cache.get.hits", description: "Cache hits");
    /// <summary>
    /// Defines the misses.
    /// </summary>
    public static readonly Counter<long> Misses = Meter.CreateCounter<long>("cache.get.misses", description: "Cache misses");
    /// <summary>
    /// Defines the set calls.
    /// </summary>
    public static readonly Counter<long> SetCalls = Meter.CreateCounter<long>("cache.set.calls", description: "Total SET operations");
    /// <summary>
    /// Defines the remove calls.
    /// </summary>
    public static readonly Counter<long> RemoveCalls = Meter.CreateCounter<long>("cache.remove.calls", description: "Total REMOVE operations");
    /// <summary>
    /// Defines the fallback to memory.
    /// </summary>
    public static readonly Counter<long> FallbackToMemory = Meter.CreateCounter<long>("cache.fallback.to_memory", description: "Circuit breaker fallback events");
    /// <summary>
    /// Defines the redis breaker opened.
    /// </summary>
    public static readonly Counter<long> RedisBreakerOpened = Meter.CreateCounter<long>("cache.redis.breaker.opened", description: "Circuit breaker opened events");
    /// <summary>
    /// Defines the stampede key rejected.
    /// </summary>
    public static readonly Counter<long> StampedeKeyRejected = Meter.CreateCounter<long>("cache.stampede.key_rejected", description: "Stampede-protected requests rejected due to suspicious/invalid key");
    /// <summary>
    /// Defines the stampede lock wait timeout.
    /// </summary>
    public static readonly Counter<long> StampedeLockWaitTimeout = Meter.CreateCounter<long>("cache.stampede.lock_wait_timeout", description: "Stampede lock wait timed out");
    /// <summary>
    /// Defines the stampede failure backoff rejected.
    /// </summary>
    public static readonly Counter<long> StampedeFailureBackoffRejected = Meter.CreateCounter<long>("cache.stampede.failure_backoff_rejected", description: "Requests rejected due to per-key failure backoff window");

    /// <summary>
    /// Defines the op ms.
    /// </summary>
    public static readonly Histogram<double> OpMs = Meter.CreateHistogram<double>("cache.op.ms", unit: "ms", description: "Cache operation latency");

    /// <summary>
    /// Defines the spill write count.
    /// </summary>
    public static readonly Counter<long> SpillWriteCount = Meter.CreateCounter<long>("cache.spill.write.count", description: "Spill write operations");
    /// <summary>
    /// Defines the spill write bytes.
    /// </summary>
    public static readonly Counter<long> SpillWriteBytes = Meter.CreateCounter<long>("cache.spill.write.bytes", unit: "bytes", description: "Spill write bytes");
    /// <summary>
    /// Defines the spill read count.
    /// </summary>
    public static readonly Counter<long> SpillReadCount = Meter.CreateCounter<long>("cache.spill.read.count", description: "Spill read operations");
    /// <summary>
    /// Defines the spill read bytes.
    /// </summary>
    public static readonly Counter<long> SpillReadBytes = Meter.CreateCounter<long>("cache.spill.read.bytes", unit: "bytes", description: "Spill read bytes");
    /// <summary>
    /// Defines the spill orphan scanned.
    /// </summary>
    public static readonly Counter<long> SpillOrphanScanned = Meter.CreateCounter<long>("cache.spill.orphan.scanned", description: "Spill files scanned for orphan cleanup");
    /// <summary>
    /// Defines the spill orphan cleanup count.
    /// </summary>
    public static readonly Counter<long> SpillOrphanCleanupCount = Meter.CreateCounter<long>("cache.spill.orphan.cleanup.count", description: "Spill files deleted during orphan cleanup");
    /// <summary>
    /// Defines the spill orphan cleanup bytes.
    /// </summary>
    public static readonly Counter<long> SpillOrphanCleanupBytes = Meter.CreateCounter<long>("cache.spill.orphan.cleanup.bytes", unit: "bytes", description: "Spill bytes deleted during orphan cleanup");
    /// <summary>
    /// Defines the spill store unavailable.
    /// </summary>
    public static readonly Counter<long> SpillStoreUnavailable = Meter.CreateCounter<long>("cache.spill.store_unavailable", description: "Spill-to-disk requested but no writable spill store is registered");
    /// <summary>
    /// Defines the set payload bytes.
    /// </summary>
    public static readonly Histogram<long> SetPayloadBytes = Meter.CreateHistogram<long>("cache.set.payload.bytes", unit: "bytes", description: "Payload size for cache SET operations");
    /// <summary>
    /// Defines the large key writes.
    /// </summary>
    public static readonly Counter<long> LargeKeyWrites = Meter.CreateCounter<long>("cache.set.large_key", description: "Large payload cache writes");
    /// <summary>
    /// Defines the evictions.
    /// </summary>
    public static readonly Counter<long> Evictions = Meter.CreateCounter<long>("cache.evictions", description: "In-memory cache evictions");

    private static ICacheBackendState? _backendState;
    private static ISpillStoreDiagnostics? _spillStoreDiagnostics;

    internal static void EnsureInitialized()
    {
        _ = CurrentBackend;
        _ = SpillActiveShards;
        _ = SpillMaxFilesInShard;
        _ = SpillImbalanceRatio;
    }

    internal static int MapBackendName(string current)
    {
        if (!BackendTypeResolver.TryParseName(current, out var backend))
            return -1;

        return backend.ToGaugeValue();
    }

    /// <summary>
    /// Initializes the current backend observable gauge. Called during service registration.
    /// </summary>
    internal static void Initialize(ICacheBackendState backendState)
    {
        _backendState = backendState;
    }

    /// <summary>
    /// Initializes spill diagnostics observables.
    /// </summary>
    internal static void InitializeSpillDiagnostics(ISpillStoreDiagnostics spillStoreDiagnostics)
    {
        _spillStoreDiagnostics = spillStoreDiagnostics;
    }

    internal static void ResetForTesting()
    {
        _backendState = null;
        _spillStoreDiagnostics = null;
    }

    internal static string GetPayloadBucket(int bytes) => bytes switch
    {
        <= 1024 => "lte_1kb",
        <= 4096 => "lte_4kb",
        <= 16384 => "lte_16kb",
        <= 65536 => "lte_64kb",
        <= 262144 => "lte_256kb",
        _ => "gt_256kb"
    };

    internal static Measurement<int> GetCurrentBackendMeasurement()
    {
        if (_backendState is null)
            return new Measurement<int>(-1, new TagList { { "backend", "unknown" } });

        var backend = _backendState.EffectiveBackend;
        return new Measurement<int>(backend.ToGaugeValue(), new TagList { { "backend", backend.ToWireName() } });
    }

    /// <summary>
    /// Observable gauge that reports the current active backend (1=redis, 0=in-memory).
    /// This allows real-time monitoring of which cache backend is actively serving requests.
    /// </summary>
    public static readonly ObservableGauge<int> CurrentBackend = Meter.CreateObservableGauge(
        "cache.current.backend",
        observeValue: static () => GetCurrentBackendMeasurement(),
        unit: "backend",
        description: "Current active cache backend (1=redis, 0=in-memory, -1=unknown)");

    /// <summary>
    /// Observable gauge for active spill shards.
    /// </summary>
    public static readonly ObservableGauge<int> SpillActiveShards = Meter.CreateObservableGauge(
        "cache.spill.shard.active",
        observeValue: () =>
        {
            var snapshot = _spillStoreDiagnostics?.GetSnapshot();
            if (snapshot is null)
                return new Measurement<int>(0, new TagList { { "mode", "unknown" } });

            return new Measurement<int>(snapshot.ActiveShards, new TagList { { "mode", snapshot.Mode } });
        },
        unit: "shards",
        description: "Active spill shard directories that currently hold at least one spill file");

    /// <summary>
    /// Observable gauge for max files in a single spill shard.
    /// </summary>
    public static readonly ObservableGauge<int> SpillMaxFilesInShard = Meter.CreateObservableGauge(
        "cache.spill.shard.max_files",
        observeValue: () =>
        {
            var snapshot = _spillStoreDiagnostics?.GetSnapshot();
            if (snapshot is null)
                return new Measurement<int>(0, new TagList { { "mode", "unknown" } });

            return new Measurement<int>(snapshot.MaxFilesInShard, new TagList { { "mode", snapshot.Mode } });
        },
        unit: "files",
        description: "Maximum spill files observed in any single shard directory");

    /// <summary>
    /// Observable gauge for spill shard imbalance ratio (max/avg).
    /// </summary>
    public static readonly ObservableGauge<double> SpillImbalanceRatio = Meter.CreateObservableGauge(
        "cache.spill.shard.imbalance_ratio",
        observeValue: () =>
        {
            var snapshot = _spillStoreDiagnostics?.GetSnapshot();
            if (snapshot is null)
                return new Measurement<double>(0d, new TagList { { "mode", "unknown" } });

            return new Measurement<double>(snapshot.ImbalanceRatio, new TagList { { "mode", snapshot.Mode } });
        },
        unit: "ratio",
        description: "Spill shard imbalance ratio (max files in shard / average files per active shard)");
}

