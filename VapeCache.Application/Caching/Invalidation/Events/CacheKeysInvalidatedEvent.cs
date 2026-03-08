using VapeCache.Core.Domain.Primitives;

namespace VapeCache.Application.Caching.Invalidation.Events;

/// <summary>
/// Domain event for direct key-based cache invalidation.
/// </summary>
public sealed record CacheKeysInvalidatedEvent(IReadOnlyList<string> Keys) : DomainEvent
{
    /// <summary>
    /// Executes cache keys invalidated event.
    /// </summary>
    public CacheKeysInvalidatedEvent(params string[] keys)
        : this((IReadOnlyList<string>)keys)
    {
    }
}
