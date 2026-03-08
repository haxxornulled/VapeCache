using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace VapeCache.Infrastructure.Connections;

/// <summary>
/// Represents the redis telemetry.
/// </summary>
public static class RedisTelemetry
{
    private const int UnknownConnectionId = -1;
    /// <summary>
    /// Executes new.
    /// </summary>
    public static readonly Meter Meter = new("VapeCache.Redis");

    /// <summary>
    /// Defines the connect attempts.
    /// </summary>
    public static readonly Counter<long> ConnectAttempts = Meter.CreateCounter<long>("redis.connect.attempts");
    /// <summary>
    /// Defines the connect failures.
    /// </summary>
    public static readonly Counter<long> ConnectFailures = Meter.CreateCounter<long>("redis.connect.failures");
    /// <summary>
    /// Defines the connect ms.
    /// </summary>
    public static readonly Histogram<double> ConnectMs = Meter.CreateHistogram<double>("redis.connect.ms");

    /// <summary>
    /// Defines the pool acquires.
    /// </summary>
    public static readonly Counter<long> PoolAcquires = Meter.CreateCounter<long>("redis.pool.acquires");
    /// <summary>
    /// Defines the pool timeouts.
    /// </summary>
    public static readonly Counter<long> PoolTimeouts = Meter.CreateCounter<long>("redis.pool.timeouts");
    /// <summary>
    /// Defines the pool wait ms.
    /// </summary>
    public static readonly Histogram<double> PoolWaitMs = Meter.CreateHistogram<double>("redis.pool.wait.ms");
    /// <summary>
    /// Defines the pool drops.
    /// </summary>
    public static readonly Counter<long> PoolDrops = Meter.CreateCounter<long>("redis.pool.drops");
    /// <summary>
    /// Defines the pool reaps.
    /// </summary>
    public static readonly Counter<long> PoolReaps = Meter.CreateCounter<long>("redis.pool.reaps");
    /// <summary>
    /// Defines the pool validations.
    /// </summary>
    public static readonly Counter<long> PoolValidations = Meter.CreateCounter<long>("redis.pool.validations");
    /// <summary>
    /// Defines the pool validation failures.
    /// </summary>
    public static readonly Counter<long> PoolValidationFailures = Meter.CreateCounter<long>("redis.pool.validation.failures");

    /// <summary>
    /// Defines the command calls.
    /// </summary>
    public static readonly Counter<long> CommandCalls = Meter.CreateCounter<long>("redis.cmd.calls");
    /// <summary>
    /// Defines the command failures.
    /// </summary>
    public static readonly Counter<long> CommandFailures = Meter.CreateCounter<long>("redis.cmd.failures");
    /// <summary>
    /// Defines the command ms.
    /// </summary>
    public static readonly Histogram<double> CommandMs = Meter.CreateHistogram<double>("redis.cmd.ms");

    /// <summary>
    /// Defines the bytes sent.
    /// </summary>
    public static readonly Counter<long> BytesSent = Meter.CreateCounter<long>("redis.bytes.sent");
    /// <summary>
    /// Defines the bytes received.
    /// </summary>
    public static readonly Counter<long> BytesReceived = Meter.CreateCounter<long>("redis.bytes.received");
    /// <summary>
    /// Defines the coalesced write batches.
    /// </summary>
    public static readonly Counter<long> CoalescedWriteBatches = Meter.CreateCounter<long>("redis.coalesced.batches");
    /// <summary>
    /// Defines the coalesced write batch bytes.
    /// </summary>
    public static readonly Histogram<long> CoalescedWriteBatchBytes = Meter.CreateHistogram<long>("redis.coalesced.batch.bytes");
    /// <summary>
    /// Defines the coalesced write batch segments.
    /// </summary>
    public static readonly Histogram<int> CoalescedWriteBatchSegments = Meter.CreateHistogram<int>("redis.coalesced.batch.segments");

    /// <summary>
    /// Defines the queue wait ms.
    /// </summary>
    public static readonly Histogram<double> QueueWaitMs = Meter.CreateHistogram<double>(
        "redis.queue.wait.ms",
        unit: "ms",
        description: "Time spent waiting for a write queue slot");

    private static readonly ConcurrentDictionary<int, Func<QueueDepthSnapshot>> QueueDepthProviders = new();
    private static readonly ConcurrentDictionary<int, Func<MuxLaneUsageSnapshot>> MuxLaneUsageProviders = new();

    /// <summary>
    /// Executes create observable gauge.
    /// </summary>
    public static readonly ObservableGauge<int> QueueDepth = Meter.CreateObservableGauge(
        "redis.queue.depth",
        ObserveQueueDepth,
        unit: "items",
        description: "Depth of Redis command queues");

    /// <summary>
    /// Executes create observable counter.
    /// </summary>
    public static readonly ObservableCounter<long> MuxLaneBytesSent = Meter.CreateObservableCounter(
        "redis.mux.lane.bytes.sent",
        ObserveMuxLaneBytesSent,
        unit: "bytes",
        description: "Cumulative bytes sent through each mux lane");

    /// <summary>
    /// Executes create observable counter.
    /// </summary>
    public static readonly ObservableCounter<long> MuxLaneBytesReceived = Meter.CreateObservableCounter(
        "redis.mux.lane.bytes.received",
        ObserveMuxLaneBytesReceived,
        unit: "bytes",
        description: "Cumulative bytes received through each mux lane");

    /// <summary>
    /// Executes create observable counter.
    /// </summary>
    public static readonly ObservableCounter<long> MuxLaneOperations = Meter.CreateObservableCounter(
        "redis.mux.lane.operations",
        ObserveMuxLaneOperations,
        unit: "operations",
        description: "Cumulative operations started on each mux lane");

    /// <summary>
    /// Executes create observable counter.
    /// </summary>
    public static readonly ObservableCounter<long> MuxLaneResponses = Meter.CreateObservableCounter(
        "redis.mux.lane.responses",
        ObserveMuxLaneResponses,
        unit: "responses",
        description: "Cumulative responses observed on each mux lane");

    /// <summary>
    /// Executes create observable counter.
    /// </summary>
    public static readonly ObservableCounter<long> MuxLaneFailures = Meter.CreateObservableCounter(
        "redis.mux.lane.failures",
        ObserveMuxLaneFailures,
        unit: "failures",
        description: "Cumulative transport/connect failures observed by each mux lane");

    /// <summary>
    /// Executes create observable counter.
    /// </summary>
    public static readonly ObservableCounter<long> MuxLaneOrphanedResponses = Meter.CreateObservableCounter(
        "redis.mux.lane.responses.orphaned",
        ObserveMuxLaneOrphanedResponses,
        unit: "responses",
        description: "Cumulative responses observed after the waiting operation already completed");

    /// <summary>
    /// Executes create observable counter.
    /// </summary>
    public static readonly ObservableCounter<long> MuxLaneResponseSequenceMismatches = Meter.CreateObservableCounter(
        "redis.mux.lane.response.sequence.mismatches",
        ObserveMuxLaneResponseSequenceMismatches,
        unit: "mismatches",
        description: "Cumulative request/response sequence mismatches detected on each mux lane");

    /// <summary>
    /// Executes create observable counter.
    /// </summary>
    public static readonly ObservableCounter<long> MuxLaneTransportResets = Meter.CreateObservableCounter(
        "redis.mux.lane.transport.resets",
        ObserveMuxLaneTransportResets,
        unit: "resets",
        description: "Cumulative transport resets observed on each mux lane");

    /// <summary>
    /// Executes create observable gauge.
    /// </summary>
    public static readonly ObservableGauge<int> MuxLaneInFlight = Meter.CreateObservableGauge(
        "redis.mux.lane.inflight",
        ObserveMuxLaneInFlight,
        unit: "operations",
        description: "Current in-flight operations on each mux lane");

    /// <summary>
    /// Executes create observable gauge.
    /// </summary>
    public static readonly ObservableGauge<double> MuxLaneInFlightUtilization = Meter.CreateObservableGauge(
        "redis.mux.lane.inflight.utilization",
        ObserveMuxLaneInFlightUtilization,
        unit: "ratio",
        description: "Current in-flight utilization (0..1) on each mux lane");

    internal static void EnsureInitialized()
    {
        _ = QueueDepth;
        _ = MuxLaneBytesSent;
        _ = MuxLaneBytesReceived;
        _ = MuxLaneOperations;
        _ = MuxLaneResponses;
        _ = MuxLaneFailures;
        _ = MuxLaneOrphanedResponses;
        _ = MuxLaneResponseSequenceMismatches;
        _ = MuxLaneTransportResets;
        _ = MuxLaneInFlight;
        _ = MuxLaneInFlightUtilization;
    }

    internal static void ResetForTesting()
    {
        QueueDepthProviders.Clear();
        MuxLaneUsageProviders.Clear();
    }

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
        if (QueueDepthProviders.IsEmpty)
        {
            yield return new Measurement<int>(
                0,
                new TagList { { "queue", "writes" }, { "connection.id", UnknownConnectionId }, { "capacity", 0 } });
            yield return new Measurement<int>(
                0,
                new TagList { { "queue", "pending" }, { "connection.id", UnknownConnectionId }, { "capacity", 0 } });
            yield break;
        }

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
        if (MuxLaneUsageProviders.IsEmpty)
        {
            yield return new Measurement<long>(
                0,
                new TagList { { "connection.id", UnknownConnectionId } });
            yield break;
        }

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
        if (MuxLaneUsageProviders.IsEmpty)
        {
            yield return new Measurement<long>(
                0,
                new TagList { { "connection.id", UnknownConnectionId } });
            yield break;
        }

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
        if (MuxLaneUsageProviders.IsEmpty)
        {
            yield return new Measurement<long>(
                0,
                new TagList { { "connection.id", UnknownConnectionId } });
            yield break;
        }

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
        if (MuxLaneUsageProviders.IsEmpty)
        {
            yield return new Measurement<long>(
                0,
                new TagList { { "connection.id", UnknownConnectionId } });
            yield break;
        }

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
        if (MuxLaneUsageProviders.IsEmpty)
        {
            yield return new Measurement<long>(
                0,
                new TagList { { "connection.id", UnknownConnectionId } });
            yield break;
        }

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
        if (MuxLaneUsageProviders.IsEmpty)
        {
            yield return new Measurement<long>(
                0,
                new TagList { { "connection.id", UnknownConnectionId } });
            yield break;
        }

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
        if (MuxLaneUsageProviders.IsEmpty)
        {
            yield return new Measurement<long>(
                0,
                new TagList { { "connection.id", UnknownConnectionId } });
            yield break;
        }

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
        if (MuxLaneUsageProviders.IsEmpty)
        {
            yield return new Measurement<long>(
                0,
                new TagList { { "connection.id", UnknownConnectionId } });
            yield break;
        }

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
        if (MuxLaneUsageProviders.IsEmpty)
        {
            yield return new Measurement<int>(
                0,
                new TagList { { "connection.id", UnknownConnectionId }, { "max_inflight", 0 } });
            yield break;
        }

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
        if (MuxLaneUsageProviders.IsEmpty)
        {
            yield return new Measurement<double>(
                0d,
                new TagList { { "connection.id", UnknownConnectionId } });
            yield break;
        }

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
