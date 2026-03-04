using System.Diagnostics.Metrics;

namespace VapeCache.Infrastructure.Connections;

internal static class RedisMetrics
{
    public static readonly Counter<long> ConnectAttempts = RedisTelemetry.ConnectAttempts;
    public static readonly Counter<long> ConnectFailures = RedisTelemetry.ConnectFailures;
    public static readonly Histogram<double> ConnectMs = RedisTelemetry.ConnectMs;

    public static readonly Counter<long> CommandCalls = RedisTelemetry.CommandCalls;
    public static readonly Counter<long> CommandFailures = RedisTelemetry.CommandFailures;
    public static readonly Histogram<double> CommandMs = RedisTelemetry.CommandMs;

    public static readonly Histogram<double> QueueWaitMs = RedisTelemetry.QueueWaitMs;

    public static KeyValuePair<string, object?>[] CreateWriteQueueWaitTags(int connectionId)
        => [new("queue", "writes"), new("connection.id", connectionId)];
}
