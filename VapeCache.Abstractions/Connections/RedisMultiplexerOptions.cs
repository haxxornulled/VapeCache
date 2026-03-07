namespace VapeCache.Abstractions.Connections;

public sealed record RedisMultiplexerOptions
{
    public int Connections { get; init; } = Math.Max(1, Environment.ProcessorCount / 2);
    public int MaxInFlightPerConnection { get; init; } = 4096;

    /// <summary>
    /// Named transport profile. Set to Custom to use explicitly configured coalescing values.
    /// </summary>
    public RedisTransportProfile TransportProfile { get; init; } = RedisTransportProfile.FullTilt;

    /// <summary>
    /// Enables OpenTelemetry metrics and distributed tracing for all Redis commands.
    /// Provides production-grade observability with minimal overhead (~1-2% CPU).
    ///
    /// Metrics: redis.cmd.calls, redis.cmd.failures, redis.cmd.ms, redis.bytes.sent/received
    /// Traces: Activity spans for each command with db.system=redis tags
    ///
    /// Default: true (observability is critical for production systems)
    /// </summary>
    public bool EnableCommandInstrumentation { get; init; } = true;

    /// <summary>
    /// Enables scatter/gather coalesced writes via SocketAsyncEventArgs when available.
    /// Uses direct non-coalesced sends when false.
    /// </summary>
    public bool EnableCoalescedSocketWrites { get; init; } = true;

    /// <summary>
    /// Enables the experimental socket-native RESP reader instead of the stream-based reader.
    /// Keep disabled unless explicitly validating this path in your environment.
    /// </summary>
    public bool EnableSocketRespReader { get; init; } = false;

    /// <summary>
    /// Runs each mux lane reader/writer loop using LongRunning worker scheduling to reduce
    /// thread-pool contention under extreme sustained load. Keep disabled by default.
    /// </summary>
    public bool UseDedicatedLaneWorkers { get; init; } = false;

    /// <summary>
    /// Maximum bytes to include in one coalesced socket write batch.
    /// Defaults to a tuned full-tilt profile (512KB). Decrease for lower single-command latency.
    /// </summary>
    public int CoalescedWriteMaxBytes { get; init; } = 512 * 1024;

    /// <summary>
    /// Maximum segment count to include in one coalesced write batch.
    /// Defaults to a tuned full-tilt profile (192 segments).
    /// </summary>
    public int CoalescedWriteMaxSegments { get; init; } = 192;

    /// <summary>
    /// Segments up to this size are copied into scratch buffers to reduce scatter/gather overhead.
    /// Defaults to a tuned full-tilt profile (1536B).
    /// </summary>
    public int CoalescedWriteSmallCopyThresholdBytes { get; init; } = 1536;

    /// <summary>
    /// Enables adaptive coalescing. Low queue depths bias for latency, high depths bias for throughput.
    /// </summary>
    public bool EnableAdaptiveCoalescing { get; init; } = true;

    /// <summary>
    /// Queue depth at or below this value uses the adaptive minimum limits.
    /// </summary>
    public int AdaptiveCoalescingLowDepth { get; init; } = 6;

    /// <summary>
    /// Queue depth at or above this value uses the configured max coalescing limits.
    /// </summary>
    public int AdaptiveCoalescingHighDepth { get; init; } = 56;

    /// <summary>
    /// Minimum bytes used when adaptive coalescing is in low-depth mode.
    /// </summary>
    public int AdaptiveCoalescingMinWriteBytes { get; init; } = 64 * 1024;

    /// <summary>
    /// Minimum segment count used when adaptive coalescing is in low-depth mode.
    /// </summary>
    public int AdaptiveCoalescingMinSegments { get; init; } = 48;

    /// <summary>
    /// Minimum scratch-copy threshold used when adaptive coalescing is in low-depth mode.
    /// </summary>
    public int AdaptiveCoalescingMinSmallCopyThresholdBytes { get; init; } = 384;

    /// <summary>
    /// Queue depth that enables burst coalescing mode.
    /// </summary>
    public int CoalescingEnterQueueDepth { get; init; } = 8;

    /// <summary>
    /// Queue depth that exits burst coalescing mode.
    /// Must be less than or equal to <see cref="CoalescingEnterQueueDepth"/>.
    /// </summary>
    public int CoalescingExitQueueDepth { get; init; } = 3;

    /// <summary>
    /// Maximum pending operations included in a single coalesced write batch.
    /// </summary>
    public int CoalescedWriteMaxOperations { get; init; } = 128;

    /// <summary>
    /// Spin iterations used to catch burst followers after the first coalesced dequeue.
    /// </summary>
    public int CoalescingSpinBudget { get; init; } = 8;

    /// <summary>
    /// Maximum time to wait for a Redis response before treating the connection as unhealthy.
    /// Set to TimeSpan.Zero to disable.
    /// </summary>
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Number of dedicated bulk lanes used for pooled bulk responses (for example GET lease/MGET-style flows).
    /// This count is carved out of the total <see cref="Connections"/> budget.
    /// Set to 0 to disable isolation and share fast lanes.
    /// </summary>
    public int BulkLaneConnections { get; init; } = 1;

    /// <summary>
    /// When true, bulk lane count is derived from <see cref="BulkLaneTargetRatio"/> and recomputed from the total lane budget.
    /// When false, <see cref="BulkLaneConnections"/> is treated as the fixed target count.
    /// </summary>
    public bool AutoAdjustBulkLanes { get; init; } = false;

    /// <summary>
    /// Target ratio of total lanes reserved as bulk lanes when <see cref="AutoAdjustBulkLanes"/> is enabled.
    /// Example: 0.25 keeps roughly 25% of all lanes as bulk-read-write.
    /// </summary>
    public double BulkLaneTargetRatio { get; init; } = 0.25;

    /// <summary>
    /// Response timeout applied to dedicated bulk lanes.
    /// This should generally be longer than fast-lane <see cref="ResponseTimeout"/>.
    /// </summary>
    public TimeSpan BulkLaneResponseTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Enables bounded autoscaling of long-lived multiplexed connections.
    /// Enterprise-only feature.
    /// </summary>
    public bool EnableAutoscaling { get; init; } = false;

    /// <summary>
    /// Minimum number of multiplexed connections to keep warm.
    /// </summary>
    public int MinConnections { get; init; } = 4;

    /// <summary>
    /// Maximum number of multiplexed connections allowed.
    /// </summary>
    public int MaxConnections { get; init; } = 16;

    /// <summary>
    /// Sampling interval for autoscaler pressure signals.
    /// </summary>
    public TimeSpan AutoscaleSampleInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Sustained high-pressure window required before scaling up.
    /// </summary>
    public TimeSpan ScaleUpWindow { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Sustained low-pressure window required before scaling down.
    /// </summary>
    public TimeSpan ScaleDownWindow { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Cooldown after a scale-up event.
    /// </summary>
    public TimeSpan ScaleUpCooldown { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Cooldown after a scale-down event.
    /// </summary>
    public TimeSpan ScaleDownCooldown { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Scale-up threshold based on average in-flight utilization per mux (0..1).
    /// </summary>
    public double ScaleUpInflightUtilization { get; init; } = 0.75;

    /// <summary>
    /// Scale-down threshold based on average in-flight utilization per mux (0..1).
    /// </summary>
    public double ScaleDownInflightUtilization { get; init; } = 0.25;

    /// <summary>
    /// Queue-depth threshold for scale-up pressure.
    /// </summary>
    public int ScaleUpQueueDepthThreshold { get; init; } = 32;

    /// <summary>
    /// Scale-up threshold for timeout rate (timeouts/sec across pool).
    /// </summary>
    public double ScaleUpTimeoutRatePerSecThreshold { get; init; } = 2.0;

    /// <summary>
    /// Scale-up threshold for rolling p99 latency (ms).
    /// </summary>
    public double ScaleUpP99LatencyMsThreshold { get; init; } = 40.0;

    /// <summary>
    /// Scale-down requires rolling p95 latency at or below this threshold (ms).
    /// </summary>
    public double ScaleDownP95LatencyMsThreshold { get; init; } = 20.0;

    /// <summary>
    /// Enables advisor mode. Decisions are logged but no scale actions are applied.
    /// Enterprise-only feature.
    /// </summary>
    public bool AutoscaleAdvisorMode { get; init; } = false;

    /// <summary>
    /// Emergency timeout-rate threshold (timeouts/sec) for immediate bounded scale-up.
    /// Enterprise-only feature.
    /// </summary>
    public double EmergencyScaleUpTimeoutRatePerSecThreshold { get; init; } = 8.0;

    /// <summary>
    /// Maximum time to wait for lane drain before removing a mux on scale-down.
    /// </summary>
    public TimeSpan ScaleDownDrainTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum active scale events allowed per rolling minute.
    /// Exceeding this freezes autoscaling temporarily.
    /// </summary>
    public int MaxScaleEventsPerMinute { get; init; } = 2;

    /// <summary>
    /// Alternating up/down scale toggles required to trigger flap protection.
    /// </summary>
    public int FlapToggleThreshold { get; init; } = 4;

    /// <summary>
    /// Freeze duration applied by guardrails (flap detection, reconnect storm, scale-rate limit).
    /// </summary>
    public TimeSpan AutoscaleFreezeDuration { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Failure-rate threshold (failures/sec across mux lanes) that triggers reconnect-storm freeze.
    /// </summary>
    public double ReconnectStormFailureRatePerSecThreshold { get; init; } = 2.0;
}
