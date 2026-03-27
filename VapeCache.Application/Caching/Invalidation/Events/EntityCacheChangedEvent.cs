using VapeCache.Core.Domain.Primitives;

namespace VapeCache.Application.Caching.Invalidation.Events;

/// <summary>
/// Domain event used to invalidate cache data associated with one entity type.
/// </summary>
public sealed record EntityCacheChangedEvent : DomainEvent
{
    public EntityCacheChangedEvent(
        string EntityName,
        IReadOnlyList<string> EntityIds,
        IReadOnlyList<string>? Zones = null,
        IReadOnlyList<string>? KeyPrefixes = null,
        IReadOnlyList<string>? Keys = null,
        IReadOnlyList<string>? Tags = null)
    {
        this.EntityName = EntityName;
        this.EntityIds = EntityIds;
        this.Zones = Zones;
        this.KeyPrefixes = KeyPrefixes;
        this.Keys = Keys;
        this.Tags = Tags;
    }

    /// <summary>
    /// Executes entity cache changed event.
    /// </summary>
    public EntityCacheChangedEvent(
        string EntityName,
        string EntityId,
        IReadOnlyList<string>? Zones = null,
        IReadOnlyList<string>? KeyPrefixes = null,
        IReadOnlyList<string>? Keys = null,
        IReadOnlyList<string>? Tags = null)
        : this(EntityName, [EntityId], Zones, KeyPrefixes, Keys, Tags)
    {
    }

    public string EntityName { get; init; }
    public IReadOnlyList<string> EntityIds { get; init; }
    public IReadOnlyList<string>? Zones { get; init; }
    public IReadOnlyList<string>? KeyPrefixes { get; init; }
    public IReadOnlyList<string>? Keys { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
}
