using VapeCache.Core.Domain.Primitives;

namespace VapeCache.Application.Caching.Invalidation.Events;

/// <summary>
/// Domain event for direct zone-based cache invalidation.
/// </summary>
public sealed record CacheZonesInvalidatedEvent(IReadOnlyList<string> Zones) : DomainEvent
{
    /// <summary>
    /// Executes cache zones invalidated event.
    /// </summary>
    public CacheZonesInvalidatedEvent(params string[] zones)
        : this((IReadOnlyList<string>)zones)
    {
    }
}
