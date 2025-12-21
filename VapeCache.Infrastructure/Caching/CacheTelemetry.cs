using System.Diagnostics.Metrics;

namespace VapeCache.Infrastructure.Caching;

public static class CacheTelemetry
{
    public static readonly Meter Meter = new("VapeCache.Cache");

    public static readonly Counter<long> GetCalls = Meter.CreateCounter<long>("cache.get.calls");
    public static readonly Counter<long> Hits = Meter.CreateCounter<long>("cache.get.hits");
    public static readonly Counter<long> Misses = Meter.CreateCounter<long>("cache.get.misses");
    public static readonly Counter<long> SetCalls = Meter.CreateCounter<long>("cache.set.calls");
    public static readonly Counter<long> RemoveCalls = Meter.CreateCounter<long>("cache.remove.calls");
    public static readonly Counter<long> FallbackToMemory = Meter.CreateCounter<long>("cache.fallback.to_memory");
    public static readonly Counter<long> RedisBreakerOpened = Meter.CreateCounter<long>("cache.redis.breaker.opened");

    public static readonly Histogram<double> OpMs = Meter.CreateHistogram<double>("cache.op.ms");
}

