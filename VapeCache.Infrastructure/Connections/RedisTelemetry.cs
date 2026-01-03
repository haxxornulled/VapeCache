using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace VapeCache.Infrastructure.Connections;

public static class RedisTelemetry
{
    public static readonly ActivitySource ActivitySource = new("VapeCache.Redis");
    public static readonly Meter Meter = new("VapeCache.Redis");

    public static readonly Counter<long> ConnectAttempts = Meter.CreateCounter<long>("redis.connect.attempts");
    public static readonly Counter<long> ConnectFailures = Meter.CreateCounter<long>("redis.connect.failures");
    public static readonly Histogram<double> ConnectMs = Meter.CreateHistogram<double>("redis.connect.ms");

    public static readonly Counter<long> PoolAcquires = Meter.CreateCounter<long>("redis.pool.acquires");
    public static readonly Counter<long> PoolTimeouts = Meter.CreateCounter<long>("redis.pool.timeouts");
    public static readonly Histogram<double> PoolWaitMs = Meter.CreateHistogram<double>("redis.pool.wait.ms");
    public static readonly Counter<long> PoolDrops = Meter.CreateCounter<long>("redis.pool.drops");
    public static readonly Counter<long> PoolReaps = Meter.CreateCounter<long>("redis.pool.reaps");
    public static readonly Counter<long> PoolValidations = Meter.CreateCounter<long>("redis.pool.validations");
    public static readonly Counter<long> PoolValidationFailures = Meter.CreateCounter<long>("redis.pool.validation.failures");

    public static readonly Counter<long> CommandCalls = Meter.CreateCounter<long>("redis.cmd.calls");
    public static readonly Counter<long> CommandFailures = Meter.CreateCounter<long>("redis.cmd.failures");
    public static readonly Histogram<double> CommandMs = Meter.CreateHistogram<double>("redis.cmd.ms");

    public static readonly Counter<long> BytesSent = Meter.CreateCounter<long>("redis.bytes.sent");
    public static readonly Counter<long> BytesReceived = Meter.CreateCounter<long>("redis.bytes.received");

    public static readonly Histogram<double> QueueWaitMs = Meter.CreateHistogram<double>(
        "redis.queue.wait.ms",
        unit: "ms",
        description: "Time spent waiting for a write queue slot");

    private static readonly ConcurrentDictionary<int, Func<QueueDepthSnapshot>> QueueDepthProviders = new();

    public static readonly ObservableGauge<int> QueueDepth = Meter.CreateObservableGauge(
        "redis.queue.depth",
        ObserveQueueDepth,
        unit: "items",
        description: "Depth of Redis command queues");

    internal static void RegisterQueueDepthProvider(int connectionId, Func<QueueDepthSnapshot> provider)
    {
        QueueDepthProviders[connectionId] = provider;
    }

    internal static void UnregisterQueueDepthProvider(int connectionId)
    {
        QueueDepthProviders.TryRemove(connectionId, out _);
    }

    private static IEnumerable<Measurement<int>> ObserveQueueDepth()
    {
        foreach (var entry in QueueDepthProviders)
        {
            var snapshot = entry.Value();
            var connectionId = entry.Key;
            yield return new Measurement<int>(
                snapshot.Writes,
                new TagList { { "queue", "writes" }, { "connection.id", connectionId }, { "capacity", snapshot.WritesCapacity } });
            yield return new Measurement<int>(
                snapshot.Pending,
                new TagList { { "queue", "pending" }, { "connection.id", connectionId }, { "capacity", snapshot.PendingCapacity } });
        }
    }

    internal readonly record struct QueueDepthSnapshot(int Writes, int Pending, int WritesCapacity, int PendingCapacity);
}
