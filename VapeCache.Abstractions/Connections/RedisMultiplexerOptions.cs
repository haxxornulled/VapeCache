namespace VapeCache.Abstractions.Connections;

public sealed record RedisMultiplexerOptions
{
    public int Connections { get; init; } = Math.Max(1, Environment.ProcessorCount / 2);
    public int MaxInFlightPerConnection { get; init; } = 4096;

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
    /// Maximum time to wait for a Redis response before treating the connection as unhealthy.
    /// Set to TimeSpan.Zero to disable.
    /// </summary>
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(2);
}
