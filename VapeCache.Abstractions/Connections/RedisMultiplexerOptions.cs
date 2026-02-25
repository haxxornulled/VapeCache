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
    /// Falls back to the legacy stream writer when false.
    /// </summary>
    public bool EnableCoalescedSocketWrites { get; init; } = true;

    /// <summary>
    /// Enables the experimental socket-native RESP reader instead of the stream-based reader.
    /// Keep disabled unless explicitly validating this path in your environment.
    /// </summary>
    public bool EnableSocketRespReader { get; init; } = false;

    /// <summary>
    /// Maximum bytes to include in one coalesced socket write batch.
    /// Defaults to a full-tilt profile (1MB). Decrease for lower single-command latency.
    /// </summary>
    public int CoalescedWriteMaxBytes { get; init; } = 1024 * 1024;

    /// <summary>
    /// Maximum segment count to include in one coalesced write batch.
    /// Defaults to a full-tilt profile (256 segments).
    /// </summary>
    public int CoalescedWriteMaxSegments { get; init; } = 256;

    /// <summary>
    /// Segments up to this size are copied into scratch buffers to reduce scatter/gather overhead.
    /// Defaults to a full-tilt profile (2KB).
    /// </summary>
    public int CoalescedWriteSmallCopyThresholdBytes { get; init; } = 2048;

    /// <summary>
    /// Enables adaptive coalescing. Low queue depths bias for latency, high depths bias for throughput.
    /// </summary>
    public bool EnableAdaptiveCoalescing { get; init; } = true;

    /// <summary>
    /// Queue depth at or below this value uses the adaptive minimum limits.
    /// </summary>
    public int AdaptiveCoalescingLowDepth { get; init; } = 4;

    /// <summary>
    /// Queue depth at or above this value uses the configured max coalescing limits.
    /// </summary>
    public int AdaptiveCoalescingHighDepth { get; init; } = 64;

    /// <summary>
    /// Minimum bytes used when adaptive coalescing is in low-depth mode.
    /// </summary>
    public int AdaptiveCoalescingMinWriteBytes { get; init; } = 64 * 1024;

    /// <summary>
    /// Minimum segment count used when adaptive coalescing is in low-depth mode.
    /// </summary>
    public int AdaptiveCoalescingMinSegments { get; init; } = 64;

    /// <summary>
    /// Minimum scratch-copy threshold used when adaptive coalescing is in low-depth mode.
    /// </summary>
    public int AdaptiveCoalescingMinSmallCopyThresholdBytes { get; init; } = 512;

    /// <summary>
    /// Maximum time to wait for a Redis response before treating the connection as unhealthy.
    /// Set to TimeSpan.Zero to disable.
    /// </summary>
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(2);

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
