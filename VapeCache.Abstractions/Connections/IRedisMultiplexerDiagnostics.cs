namespace VapeCache.Abstractions.Connections;

public interface IRedisMultiplexerDiagnostics
{
    RedisAutoscalerSnapshot GetAutoscalerSnapshot();
}

public sealed record RedisAutoscalerSnapshot(
    bool Enabled,
    int CurrentConnections,
    int TargetConnections,
    int MinConnections,
    int MaxConnections,
    int CurrentReadLanes,
    int CurrentWriteLanes,
    int HighSignalCount,
    double AvgInflightUtilization,
    double AvgQueueDepth,
    int MaxQueueDepth,
    double TimeoutRatePerSec,
    double RollingP95LatencyMs,
    double RollingP99LatencyMs,
    int UnhealthyConnections,
    double ReconnectFailureRatePerSec,
    int ScaleEventsInCurrentMinute,
    int MaxScaleEventsPerMinute,
    bool Frozen,
    DateTimeOffset? FrozenUntilUtc,
    string? FreezeReason,
    DateTimeOffset? LastScaleEventUtc,
    string? LastScaleDirection,
    string? LastScaleReason);
