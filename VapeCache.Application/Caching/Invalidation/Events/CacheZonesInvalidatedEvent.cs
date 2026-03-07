using VapeCache.Core.Domain.Primitives;

namespace VapeCache.Application.Caching.Invalidation.Events;

/// <summary>
/// Domain event for direct zone-based cache invalidation.
/// </summary>
public sealed record CacheZonesInvalidatedEvent(IReadOnlyList<string> Zones) : DomainEvent
{
    public CacheZonesInvalidatedEvent(params string[] zones)
        : this((IReadOnlyList<string>)zones)
    {
    }
}
