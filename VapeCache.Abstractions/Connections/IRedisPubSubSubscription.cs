namespace VapeCache.Abstractions.Connections;

/// <summary>
/// Represents an active pub/sub subscription.
/// </summary>
public interface IRedisPubSubSubscription : IAsyncDisposable
{
    /// <summary>
    /// Gets the subscribed channel name.
    /// </summary>
    string Channel { get; }
}
