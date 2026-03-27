using VapeCache.Core.Domain.Primitives;

namespace VapeCache.Application.Caching.Invalidation.Events;

/// <summary>
/// Domain event for direct key-based cache invalidation.
/// </summary>
public sealed record CacheKeysInvalidatedEvent : DomainEvent
{
    public CacheKeysInvalidatedEvent(IReadOnlyList<string> Keys)
    {
        this.Keys = Keys;
    }

    /// <summary>
    /// Executes cache keys invalidated event.
    /// </summary>
    public CacheKeysInvalidatedEvent(params string[] Keys)
        : this((IReadOnlyList<string>)Keys)
    {
    }

    public IReadOnlyList<string> Keys { get; init; }
}
