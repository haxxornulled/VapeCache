using VapeCache.Core.Domain.Primitives;

namespace VapeCache.Application.Caching.Invalidation.Events;

/// <summary>
/// Domain event for direct zone-based cache invalidation.
/// </summary>
public sealed record CacheZonesInvalidatedEvent : DomainEvent
{
    public CacheZonesInvalidatedEvent(IReadOnlyList<string> Zones)
    {
        this.Zones = Zones;
    }

    /// <summary>
    /// Executes cache zones invalidated event.
    /// </summary>
    public CacheZonesInvalidatedEvent(params string[] Zones)
        : this((IReadOnlyList<string>)Zones)
    {
    }

    public IReadOnlyList<string> Zones { get; init; }
}
