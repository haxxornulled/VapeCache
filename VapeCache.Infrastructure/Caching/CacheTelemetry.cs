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

    public static readonly Histogram<double> OpMs = Meter.CreateHistogram<double>("cache.op.ms", unit: "ms", description: "Cache operation latency");

    public static readonly Counter<long> SpillWriteCount = Meter.CreateCounter<long>("cache.spill.write.count", description: "Spill write operations");
    public static readonly Counter<long> SpillWriteBytes = Meter.CreateCounter<long>("cache.spill.write.bytes", unit: "bytes", description: "Spill write bytes");
    public static readonly Counter<long> SpillReadCount = Meter.CreateCounter<long>("cache.spill.read.count", description: "Spill read operations");
    public static readonly Counter<long> SpillReadBytes = Meter.CreateCounter<long>("cache.spill.read.bytes", unit: "bytes", description: "Spill read bytes");
    public static readonly Counter<long> SpillOrphanScanned = Meter.CreateCounter<long>("cache.spill.orphan.scanned", description: "Spill files scanned for orphan cleanup");
    public static readonly Counter<long> SpillOrphanCleanupCount = Meter.CreateCounter<long>("cache.spill.orphan.cleanup.count", description: "Spill files deleted during orphan cleanup");
    public static readonly Counter<long> SpillOrphanCleanupBytes = Meter.CreateCounter<long>("cache.spill.orphan.cleanup.bytes", unit: "bytes", description: "Spill bytes deleted during orphan cleanup");

    private static ICurrentCacheService? _currentCacheService;

    /// <summary>
    /// Initializes the current backend observable gauge. Called during service registration.
    /// </summary>
    internal static void Initialize(ICurrentCacheService currentCacheService)
    {
        _currentCacheService = currentCacheService;
    }

    /// <summary>
    /// Observable gauge that reports the current active backend (1=redis, 0=in-memory).
    /// This allows real-time monitoring of which cache backend is actively serving requests.
    /// </summary>
    public static readonly ObservableGauge<int> CurrentBackend = Meter.CreateObservableGauge(
        "cache.current.backend",
        observeValue: () =>
        {
            if (_currentCacheService is null)
                return new Measurement<int>(0, new TagList { { "backend", "unknown" } });

            var current = _currentCacheService.CurrentName;
            var value = current switch
            {
                "redis" => 1,
                "in-memory" => 0,
                _ => -1
            };

            return new Measurement<int>(value, new TagList { { "backend", current } });
        },
        unit: "backend",
        description: "Current active cache backend (1=redis, 0=in-memory, -1=unknown)");
}

