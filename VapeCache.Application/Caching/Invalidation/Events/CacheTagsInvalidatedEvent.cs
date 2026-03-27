using VapeCache.Core.Domain.Primitives;

namespace VapeCache.Application.Caching.Invalidation.Events;

/// <summary>
/// Domain event for direct tag-based cache invalidation.
/// </summary>
public sealed record CacheTagsInvalidatedEvent : DomainEvent
{
    public CacheTagsInvalidatedEvent(IReadOnlyList<string> Tags)
    {
        this.Tags = Tags;
    }

    /// <summary>
    /// Executes cache tags invalidated event.
    /// </summary>
    public CacheTagsInvalidatedEvent(params string[] Tags)
        : this((IReadOnlyList<string>)Tags)
    {
    }

    public IReadOnlyList<string> Tags { get; init; }
}
