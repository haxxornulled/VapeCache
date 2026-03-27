namespace VapeCache.Abstractions.Connections;

/// <summary>
/// Represents a message received from a Redis pub/sub channel.
/// </summary>
public readonly record struct RedisPubSubMessage
{
    public RedisPubSubMessage(string channel, ReadOnlyMemory<byte> payload, DateTimeOffset receivedAtUtc)
    {
        Channel = channel;
        Payload = payload;
        ReceivedAtUtc = receivedAtUtc;
    }

    public string Channel { get; init; }
    public ReadOnlyMemory<byte> Payload { get; init; }
    public DateTimeOffset ReceivedAtUtc { get; init; }
}
