namespace VapeCache.Core.Domain.Primitives;

/// <summary>
/// Base immutable domain-event record.
/// </summary>
/// <param name="OccurredOnUtc">UTC timestamp when the event occurred.</param>
public abstract record DomainEvent(DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    /// <summary>
    /// Initializes the event with the current UTC timestamp.
    /// </summary>
    protected DomainEvent() : this(DateTimeOffset.UtcNow)
    {
    }
}
