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
    public static readonly Counter<long> CoalescedWriteBatches = Meter.CreateCounter<long>("redis.coalesced.batches");
    public static readonly Histogram<long> CoalescedWriteBatchBytes = Meter.CreateHistogram<long>("redis.coalesced.batch.bytes");
    public static readonly Histogram<int> CoalescedWriteBatchSegments = Meter.CreateHistogram<int>("redis.coalesced.batch.segments");

    public static readonly Histogram<double> QueueWaitMs = Meter.CreateHistogram<double>(
        "redis.queue.wait.ms",
        unit: "ms",
        description: "Time spent waiting for a write queue slot");

    private static readonly ConcurrentDictionary<int, Func<QueueDepthSnapshot>> QueueDepthProviders = new();
    private static readonly ConcurrentDictionary<int, Func<MuxLaneUsageSnapshot>> MuxLaneUsageProviders = new();

    public static readonly ObservableGauge<int> QueueDepth = Meter.CreateObservableGauge(
        "redis.queue.depth",
        ObserveQueueDepth,
        unit: "items",
        description: "Depth of Redis command queues");

    public static readonly ObservableCounter<long> MuxLaneBytesSent = Meter.CreateObservableCounter(
        "redis.mux.lane.bytes.sent",
        ObserveMuxLaneBytesSent,
        unit: "bytes",
        description: "Cumulative bytes sent through each mux lane");

    public static readonly ObservableCounter<long> MuxLaneBytesReceived = Meter.CreateObservableCounter(
        "redis.mux.lane.bytes.received",
        ObserveMuxLaneBytesReceived,
        unit: "bytes",
        description: "Cumulative bytes received through each mux lane");

    public static readonly ObservableCounter<long> MuxLaneOperations = Meter.CreateObservableCounter(
        "redis.mux.lane.operations",
        ObserveMuxLaneOperations,
        unit: "operations",
        description: "Cumulative operations started on each mux lane");

    public static readonly ObservableCounter<long> MuxLaneResponses = Meter.CreateObservableCounter(
        "redis.mux.lane.responses",
        ObserveMuxLaneResponses,
        unit: "responses",
        description: "Cumulative responses observed on each mux lane");

    public static readonly ObservableCounter<long> MuxLaneFailures = Meter.CreateObservableCounter(
        "redis.mux.lane.failures",
        ObserveMuxLaneFailures,
        unit: "failures",
        description: "Cumulative transport/connect failures observed by each mux lane");

    public static readonly ObservableCounter<long> MuxLaneOrphanedResponses = Meter.CreateObservableCounter(
        "redis.mux.lane.responses.orphaned",
        ObserveMuxLaneOrphanedResponses,
        unit: "responses",
        description: "Cumulative responses observed after the waiting operation already completed");

    public static readonly ObservableCounter<long> MuxLaneResponseSequenceMismatches = Meter.CreateObservableCounter(
        "redis.mux.lane.response.sequence.mismatches",
        ObserveMuxLaneResponseSequenceMismatches,
        unit: "mismatches",
        description: "Cumulative request/response sequence mismatches detected on each mux lane");

    public static readonly ObservableCounter<long> MuxLaneTransportResets = Meter.CreateObservableCounter(
        "redis.mux.lane.transport.resets",
        ObserveMuxLaneTransportResets,
        unit: "resets",
        description: "Cumulative transport resets observed on each mux lane");

    public static readonly ObservableGauge<int> MuxLaneInFlight = Meter.CreateObservableGauge(
        "redis.mux.lane.inflight",
        ObserveMuxLaneInFlight,
        unit: "operations",
        description: "Current in-flight operations on each mux lane");

    public static readonly ObservableGauge<double> MuxLaneInFlightUtilization = Meter.CreateObservableGauge(
        "redis.mux.lane.inflight.utilization",
        ObserveMuxLaneInFlightUtilization,
        unit: "ratio",
        description: "Current in-flight utilization (0..1) on each mux lane");

    internal static void RegisterQueueDepthProvider(int connectionId, Func<QueueDepthSnapshot> provider)
    {
        QueueDepthProviders[connectionId] = provider;
    }

    internal static void UnregisterQueueDepthProvider(int connectionId)
    {
        QueueDepthProviders.TryRemove(connectionId, out _);
    }

    internal static void RegisterMuxLaneUsageProvider(int connectionId, Func<MuxLaneUsageSnapshot> provider)
    {
        MuxLaneUsageProviders[connectionId] = provider;
    }

    internal static void UnregisterMuxLaneUsageProvider(int connectionId)
    {
        MuxLaneUsageProviders.TryRemove(connectionId, out _);
    }

    private static IEnumerable<Measurement<int>> ObserveQueueDepth()
    {
        foreach (var entry in QueueDepthProviders)
        {
            QueueDepthSnapshot snapshot;
            try
            {
                snapshot = entry.Value();
            }
            catch
            {
                continue;
            }
            var connectionId = entry.Key;
            yield return new Measurement<int>(
                snapshot.Writes,
                new TagList { { "queue", "writes" }, { "connection.id", connectionId }, { "capacity", snapshot.WritesCapacity } });
            yield return new Measurement<int>(
                snapshot.Pending,
                new TagList { { "queue", "pending" }, { "connection.id", connectionId }, { "capacity", snapshot.PendingCapacity } });
        }
    }

    private static IEnumerable<Measurement<long>> ObserveMuxLaneBytesSent()
    {
        foreach (var entry in MuxLaneUsageProviders)
        {
            MuxLaneUsageSnapshot snapshot;
            try
            {
                snapshot = entry.Value();
            }
            catch
            {
                continue;
            }
            yield return new Measurement<long>(
                snapshot.BytesSent,
                new TagList { { "connection.id", entry.Key } });
        }
    }

    private static IEnumerable<Measurement<long>> ObserveMuxLaneBytesReceived()
    {
        foreach (var entry in MuxLaneUsageProviders)
        {
            MuxLaneUsageSnapshot snapshot;
            try
            {
                snapshot = entry.Value();
            }
            catch
            {
                continue;
            }
            yield return new Measurement<long>(
                snapshot.BytesReceived,
                new TagList { { "connection.id", entry.Key } });
        }
    }

    private static IEnumerable<Measurement<long>> ObserveMuxLaneOperations()
    {
        foreach (var entry in MuxLaneUsageProviders)
        {
            MuxLaneUsageSnapshot snapshot;
            try
            {
                snapshot = entry.Value();
            }
            catch
            {
                continue;
            }
            yield return new Measurement<long>(
                snapshot.Operations,
                new TagList { { "connection.id", entry.Key } });
        }
    }

    private static IEnumerable<Measurement<long>> ObserveMuxLaneFailures()
    {
        foreach (var entry in MuxLaneUsageProviders)
        {
            MuxLaneUsageSnapshot snapshot;
            try
            {
                snapshot = entry.Value();
            }
            catch
            {
                continue;
            }
            yield return new Measurement<long>(
                snapshot.Failures,
                new TagList { { "connection.id", entry.Key } });
        }
    }

    private static IEnumerable<Measurement<long>> ObserveMuxLaneResponses()
    {
        foreach (var entry in MuxLaneUsageProviders)
        {
            MuxLaneUsageSnapshot snapshot;
            try
            {
                snapshot = entry.Value();
            }
            catch
            {
                continue;
            }
            yield return new Measurement<long>(
                snapshot.Responses,
                new TagList { { "connection.id", entry.Key } });
        }
    }

    private static IEnumerable<Measurement<long>> ObserveMuxLaneOrphanedResponses()
    {
        foreach (var entry in MuxLaneUsageProviders)
        {
            MuxLaneUsageSnapshot snapshot;
            try
            {
                snapshot = entry.Value();
            }
            catch
            {
                continue;
            }
            yield return new Measurement<long>(
                snapshot.OrphanedResponses,
                new TagList { { "connection.id", entry.Key } });
        }
    }

    private static IEnumerable<Measurement<long>> ObserveMuxLaneResponseSequenceMismatches()
    {
        foreach (var entry in MuxLaneUsageProviders)
        {
            MuxLaneUsageSnapshot snapshot;
            try
            {
                snapshot = entry.Value();
            }
            catch
            {
                continue;
            }
            yield return new Measurement<long>(
                snapshot.ResponseSequenceMismatches,
                new TagList { { "connection.id", entry.Key } });
        }
    }

    private static IEnumerable<Measurement<long>> ObserveMuxLaneTransportResets()
    {
        foreach (var entry in MuxLaneUsageProviders)
        {
            MuxLaneUsageSnapshot snapshot;
            try
            {
                snapshot = entry.Value();
            }
            catch
            {
                continue;
            }
            yield return new Measurement<long>(
                snapshot.TransportResets,
                new TagList { { "connection.id", entry.Key } });
        }
    }

    private static IEnumerable<Measurement<int>> ObserveMuxLaneInFlight()
    {
        foreach (var entry in MuxLaneUsageProviders)
        {
            MuxLaneUsageSnapshot snapshot;
            try
            {
                snapshot = entry.Value();
            }
            catch
            {
                continue;
            }
            yield return new Measurement<int>(
                snapshot.InFlight,
                new TagList { { "connection.id", entry.Key }, { "max_inflight", snapshot.MaxInFlight } });
        }
    }

    private static IEnumerable<Measurement<double>> ObserveMuxLaneInFlightUtilization()
    {
        foreach (var entry in MuxLaneUsageProviders)
        {
            MuxLaneUsageSnapshot snapshot;
            try
            {
                snapshot = entry.Value();
            }
            catch
            {
                continue;
            }
            var utilization = snapshot.MaxInFlight <= 0
                ? 0d
                : (double)snapshot.InFlight / snapshot.MaxInFlight;
            yield return new Measurement<double>(
                utilization,
                new TagList { { "connection.id", entry.Key } });
        }
    }

    internal readonly record struct QueueDepthSnapshot(int Writes, int Pending, int WritesCapacity, int PendingCapacity);
    internal readonly record struct MuxLaneUsageSnapshot(
        long BytesSent,
        long BytesReceived,
        long Operations,
        long Failures,
        long Responses,
        long OrphanedResponses,
        long ResponseSequenceMismatches,
        long TransportResets,
        int InFlight,
        int MaxInFlight);
}
