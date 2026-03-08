namespace VapeCache.Core.Domain.Primitives;

/// <summary>
/// Marker contract for domain events emitted by aggregate roots.
/// </summary>
public interface IDomainEvent
{
    /// <summary>
    /// Gets when the event occurred in UTC.
    /// </summary>
    DateTimeOffset OccurredOnUtc { get; }
}
