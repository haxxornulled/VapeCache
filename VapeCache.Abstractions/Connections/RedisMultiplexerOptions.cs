namespace VapeCache.Abstractions.Connections;

public sealed record RedisMultiplexerOptions
{
    public int Connections { get; init; } = Math.Max(1, Environment.ProcessorCount / 2);
    public int MaxInFlightPerConnection { get; init; } = 4096;

    /// <summary>
    /// If false, command-level metrics/tracing are skipped to minimize allocations in hot paths.
    /// </summary>
    public bool EnableCommandInstrumentation { get; init; } = false;

    /// <summary>
    /// Enables scatter/gather coalesced writes via SocketAsyncEventArgs when available.
    /// Falls back to the legacy stream writer when false.
    /// </summary>
    public bool EnableCoalescedSocketWrites { get; init; } = true;
}
