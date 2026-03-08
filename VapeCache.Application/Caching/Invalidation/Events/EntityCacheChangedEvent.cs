using VapeCache.Core.Domain.Primitives;

namespace VapeCache.Application.Caching.Invalidation.Events;

/// <summary>
/// Domain event used to invalidate cache data associated with one entity type.
/// </summary>
public sealed record EntityCacheChangedEvent(
    string EntityName,
    IReadOnlyList<string> EntityIds,
    IReadOnlyList<string>? Zones = null,
    IReadOnlyList<string>? KeyPrefixes = null,
    IReadOnlyList<string>? Keys = null,
    IReadOnlyList<string>? Tags = null) : DomainEvent
{
    /// <summary>
    /// Executes entity cache changed event.
    /// </summary>
    public EntityCacheChangedEvent(
        string entityName,
        string entityId,
        IReadOnlyList<string>? zones = null,
        IReadOnlyList<string>? keyPrefixes = null,
        IReadOnlyList<string>? keys = null,
        IReadOnlyList<string>? tags = null)
        : this(entityName, [entityId], zones, keyPrefixes, keys, tags)
    {
    }
}
