namespace VapeCache.Abstractions.Connections;

/// <summary>
/// Controls Redis pub/sub delivery behavior.
/// </summary>
public sealed record RedisPubSubOptions
{
    /// <summary>
    /// Enables Redis pub/sub service registration and message processing.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Per-subscription delivery queue capacity before backpressure handling applies.
    /// </summary>
    public int DeliveryQueueCapacity { get; init; } = 512;

    /// <summary>
    /// When true, oldest queued message is dropped first when the queue is full.
    /// When false, the newest message is dropped.
    /// </summary>
    public bool DropOldestOnBackpressure { get; init; } = true;

    /// <summary>
    /// Initial delay before reconnecting the subscriber connection after failures.
    /// </summary>
    public TimeSpan ReconnectDelayMin { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Maximum reconnect backoff delay for subscriber connection retries.
    /// </summary>
    public TimeSpan ReconnectDelayMax { get; init; } = TimeSpan.FromSeconds(5);
}
