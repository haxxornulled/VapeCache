using System.Diagnostics;
using System.Diagnostics.Metrics;
using VapeCache.Abstractions.Caching;

namespace VapeCache.Infrastructure.Caching;

public static class CacheTelemetry
{
    public static readonly Meter Meter = new("VapeCache.Cache");

    public static readonly Counter<long> GetCalls = Meter.CreateCounter<long>("cache.get.calls", description: "Total GET operations");
    public static readonly Counter<long> Hits = Meter.CreateCounter<long>("cache.get.hits", description: "Cache hits");
    public static readonly Counter<long> Misses = Meter.CreateCounter<long>("cache.get.misses", description: "Cache misses");
    public static readonly Counter<long> SetCalls = Meter.CreateCounter<long>("cache.set.calls", description: "Total SET operations");
    public static readonly Counter<long> RemoveCalls = Meter.CreateCounter<long>("cache.remove.calls", description: "Total REMOVE operations");
    public static readonly Counter<long> FallbackToMemory = Meter.CreateCounter<long>("cache.fallback.to_memory", description: "Circuit breaker fallback events");
    public static readonly Counter<long> RedisBreakerOpened = Meter.CreateCounter<long>("cache.redis.breaker.opened", description: "Circuit breaker opened events");
    public static readonly Counter<long> StampedeKeyRejected = Meter.CreateCounter<long>("cache.stampede.key_rejected", description: "Stampede-protected requests rejected due to suspicious/invalid key");
    public static readonly Counter<long> StampedeLockWaitTimeout = Meter.CreateCounter<long>("cache.stampede.lock_wait_timeout", description: "Stampede lock wait timed out");
    public static readonly Counter<long> StampedeFailureBackoffRejected = Meter.CreateCounter<long>("cache.stampede.failure_backoff_rejected", description: "Requests rejected due to per-key failure backoff window");

    public static readonly Histogram<double> OpMs = Meter.CreateHistogram<double>("cache.op.ms", unit: "ms", description: "Cache operation latency");

    public static readonly Counter<long> SpillWriteCount = Meter.CreateCounter<long>("cache.spill.write.count", description: "Spill write operations");
    public static readonly Counter<long> SpillWriteBytes = Meter.CreateCounter<long>("cache.spill.write.bytes", unit: "bytes", description: "Spill write bytes");
    public static readonly Counter<long> SpillReadCount = Meter.CreateCounter<long>("cache.spill.read.count", description: "Spill read operations");
    public static readonly Counter<long> SpillReadBytes = Meter.CreateCounter<long>("cache.spill.read.bytes", unit: "bytes", description: "Spill read bytes");
    public static readonly Counter<long> SpillOrphanScanned = Meter.CreateCounter<long>("cache.spill.orphan.scanned", description: "Spill files scanned for orphan cleanup");
    public static readonly Counter<long> SpillOrphanCleanupCount = Meter.CreateCounter<long>("cache.spill.orphan.cleanup.count", description: "Spill files deleted during orphan cleanup");
    public static readonly Counter<long> SpillOrphanCleanupBytes = Meter.CreateCounter<long>("cache.spill.orphan.cleanup.bytes", unit: "bytes", description: "Spill bytes deleted during orphan cleanup");
    public static readonly Counter<long> SpillStoreUnavailable = Meter.CreateCounter<long>("cache.spill.store_unavailable", description: "Spill-to-disk requested but no writable spill store is registered");
    public static readonly Histogram<long> SetPayloadBytes = Meter.CreateHistogram<long>("cache.set.payload.bytes", unit: "bytes", description: "Payload size for cache SET operations");
    public static readonly Counter<long> LargeKeyWrites = Meter.CreateCounter<long>("cache.set.large_key", description: "Large payload cache writes");
    public static readonly Counter<long> Evictions = Meter.CreateCounter<long>("cache.evictions", description: "In-memory cache evictions");

    private static ICurrentCacheService? _currentCacheService;
    private static ISpillStoreDiagnostics? _spillStoreDiagnostics;

    internal static int MapBackendName(string current) => current switch
    {
        "redis" => 1,
        "memory" => 0,
        "in-memory" => 0,
        _ => -1
    };

    /// <summary>
    /// Initializes the current backend observable gauge. Called during service registration.
    /// </summary>
    internal static void Initialize(ICurrentCacheService currentCacheService)
    {
        _currentCacheService = currentCacheService;
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
        _currentCacheService = null;
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
        if (_currentCacheService is null)
            return new Measurement<int>(-1, new TagList { { "backend", "unknown" } });

        var current = _currentCacheService.CurrentName;
        var value = MapBackendName(current);

        return new Measurement<int>(value, new TagList { { "backend", current } });
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

