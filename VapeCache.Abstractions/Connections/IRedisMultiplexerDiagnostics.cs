namespace VapeCache.Abstractions.Connections;

public interface IRedisMultiplexerDiagnostics
{
    RedisAutoscalerSnapshot GetAutoscalerSnapshot();
    IReadOnlyList<RedisMuxLaneSnapshot> GetMuxLaneSnapshots();
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

public sealed record RedisMuxLaneSnapshot(
    int LaneIndex,
    int ConnectionId,
    string Role,
    int WriteQueueDepth,
    int InFlight,
    int MaxInFlight,
    double InFlightUtilization,
    long BytesSent,
    long BytesReceived,
    long Operations,
    long Failures,
    long Responses,
    long OrphanedResponses,
    long ResponseSequenceMismatches,
    long TransportResets,
    bool Healthy);
