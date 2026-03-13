namespace VapeCache.Abstractions.Connections;

/// <summary>
/// Represents a message received from a Redis pub/sub channel.
/// </summary>
public readonly record struct RedisPubSubMessage(
    string Channel,
    ReadOnlyMemory<byte> Payload,
    DateTimeOffset ReceivedAtUtc);
