namespace VapeCache.Abstractions.Connections;

/// <summary>
/// Provides Redis pub/sub publish and subscribe operations.
/// </summary>
public interface IRedisPubSubService : IAsyncDisposable
{
    /// <summary>
    /// Publishes a message payload to a channel.
    /// Returns the number of subscribers that received the message.
    /// </summary>
    ValueTask<long> PublishAsync(string channel, ReadOnlyMemory<byte> payload, CancellationToken ct = default);

    /// <summary>
    /// Subscribes to a channel and invokes the handler for each message.
    /// Dispose the returned subscription to unsubscribe this handler.
    /// </summary>
    ValueTask<IRedisPubSubSubscription> SubscribeAsync(
        string channel,
        Func<RedisPubSubMessage, CancellationToken, ValueTask> handler,
        CancellationToken ct = default);
}
