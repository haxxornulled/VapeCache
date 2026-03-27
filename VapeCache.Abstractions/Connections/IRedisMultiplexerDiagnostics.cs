namespace VapeCache.Abstractions.Connections;

/// <summary>
/// Defines the redis multiplexer diagnostics contract.
/// </summary>
public interface IRedisMultiplexerDiagnostics
{
    /// <summary>
    /// Executes get autoscaler snapshot.
    /// </summary>
    RedisAutoscalerSnapshot GetAutoscalerSnapshot();
    /// <summary>
    /// Executes get mux lane snapshots.
    /// </summary>
    IReadOnlyList<RedisMuxLaneSnapshot> GetMuxLaneSnapshots();
}

/// <summary>
/// Represents the redis autoscaler snapshot.
/// </summary>
public sealed record RedisAutoscalerSnapshot
{
    public RedisAutoscalerSnapshot(
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
        string? LastScaleReason,
        int SpillSignalCount = 0,
        long SpillTotalFiles = 0,
        int SpillActiveShards = 0,
        double SpillImbalanceRatio = 0d,
        double PressureScore = 0d,
        string PressureTier = "normal")
    {
        this.Enabled = Enabled;
        this.CurrentConnections = CurrentConnections;
        this.TargetConnections = TargetConnections;
        this.MinConnections = MinConnections;
        this.MaxConnections = MaxConnections;
        this.CurrentReadLanes = CurrentReadLanes;
        this.CurrentWriteLanes = CurrentWriteLanes;
        this.HighSignalCount = HighSignalCount;
        this.AvgInflightUtilization = AvgInflightUtilization;
        this.AvgQueueDepth = AvgQueueDepth;
        this.MaxQueueDepth = MaxQueueDepth;
        this.TimeoutRatePerSec = TimeoutRatePerSec;
        this.RollingP95LatencyMs = RollingP95LatencyMs;
        this.RollingP99LatencyMs = RollingP99LatencyMs;
        this.UnhealthyConnections = UnhealthyConnections;
        this.ReconnectFailureRatePerSec = ReconnectFailureRatePerSec;
        this.ScaleEventsInCurrentMinute = ScaleEventsInCurrentMinute;
        this.MaxScaleEventsPerMinute = MaxScaleEventsPerMinute;
        this.Frozen = Frozen;
        this.FrozenUntilUtc = FrozenUntilUtc;
        this.FreezeReason = FreezeReason;
        this.LastScaleEventUtc = LastScaleEventUtc;
        this.LastScaleDirection = LastScaleDirection;
        this.LastScaleReason = LastScaleReason;
        this.SpillSignalCount = SpillSignalCount;
        this.SpillTotalFiles = SpillTotalFiles;
        this.SpillActiveShards = SpillActiveShards;
        this.SpillImbalanceRatio = SpillImbalanceRatio;
        this.PressureScore = PressureScore;
        this.PressureTier = PressureTier;
    }

    public bool Enabled { get; init; }
    public int CurrentConnections { get; init; }
    public int TargetConnections { get; init; }
    public int MinConnections { get; init; }
    public int MaxConnections { get; init; }
    public int CurrentReadLanes { get; init; }
    public int CurrentWriteLanes { get; init; }
    public int HighSignalCount { get; init; }
    public double AvgInflightUtilization { get; init; }
    public double AvgQueueDepth { get; init; }
    public int MaxQueueDepth { get; init; }
    public double TimeoutRatePerSec { get; init; }
    public double RollingP95LatencyMs { get; init; }
    public double RollingP99LatencyMs { get; init; }
    public int UnhealthyConnections { get; init; }
    public double ReconnectFailureRatePerSec { get; init; }
    public int ScaleEventsInCurrentMinute { get; init; }
    public int MaxScaleEventsPerMinute { get; init; }
    public bool Frozen { get; init; }
    public DateTimeOffset? FrozenUntilUtc { get; init; }
    public string? FreezeReason { get; init; }
    public DateTimeOffset? LastScaleEventUtc { get; init; }
    public string? LastScaleDirection { get; init; }
    public string? LastScaleReason { get; init; }
    public int SpillSignalCount { get; init; }
    public long SpillTotalFiles { get; init; }
    public int SpillActiveShards { get; init; }
    public double SpillImbalanceRatio { get; init; }
    public double PressureScore { get; init; }
    public string PressureTier { get; init; }
}

/// <summary>
/// Represents the redis mux lane snapshot.
/// </summary>
public sealed record RedisMuxLaneSnapshot
{
    public RedisMuxLaneSnapshot(
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
        bool Healthy)
    {
        this.LaneIndex = LaneIndex;
        this.ConnectionId = ConnectionId;
        this.Role = Role;
        this.WriteQueueDepth = WriteQueueDepth;
        this.InFlight = InFlight;
        this.MaxInFlight = MaxInFlight;
        this.InFlightUtilization = InFlightUtilization;
        this.BytesSent = BytesSent;
        this.BytesReceived = BytesReceived;
        this.Operations = Operations;
        this.Failures = Failures;
        this.Responses = Responses;
        this.OrphanedResponses = OrphanedResponses;
        this.ResponseSequenceMismatches = ResponseSequenceMismatches;
        this.TransportResets = TransportResets;
        this.Healthy = Healthy;
    }

    public int LaneIndex { get; init; }
    public int ConnectionId { get; init; }
    public string Role { get; init; }
    public int WriteQueueDepth { get; init; }
    public int InFlight { get; init; }
    public int MaxInFlight { get; init; }
    public double InFlightUtilization { get; init; }
    public long BytesSent { get; init; }
    public long BytesReceived { get; init; }
    public long Operations { get; init; }
    public long Failures { get; init; }
    public long Responses { get; init; }
    public long OrphanedResponses { get; init; }
    public long ResponseSequenceMismatches { get; init; }
    public long TransportResets { get; init; }
    public bool Healthy { get; init; }
}
