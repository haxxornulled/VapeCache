using VapeCache.Core.Domain.Primitives;

namespace VapeCache.Application.Caching.Invalidation.Events;

/// <summary>
/// Domain event for direct tag-based cache invalidation.
/// </summary>
public sealed record CacheTagsInvalidatedEvent(IReadOnlyList<string> Tags) : DomainEvent
{
    public CacheTagsInvalidatedEvent(params string[] tags)
        : this((IReadOnlyList<string>)tags)
    {
    }
}
