namespace VapeCache.Core.Domain.Primitives;

/// <summary>
/// Base aggregate-root primitive that tracks domain events raised during a unit of work.
/// </summary>
public abstract class AggregateRoot<TId> : Entity<TId>
    where TId : notnull
{
    private readonly List<IDomainEvent> _domainEvents = [];

    /// <summary>
    /// Initializes the aggregate root with an identifier.
    /// </summary>
    protected AggregateRoot(TId id) : base(id)
    {
    }

    /// <summary>
    /// Gets events raised by this aggregate since the last dequeue.
    /// </summary>
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents;

    /// <summary>
    /// Raises a domain event for this aggregate.
    /// </summary>
    protected void RaiseDomainEvent(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _domainEvents.Add(domainEvent);
    }

    /// <summary>
    /// Returns and clears all currently buffered domain events.
    /// </summary>
    public IReadOnlyCollection<IDomainEvent> DequeueDomainEvents()
    {
        if (_domainEvents.Count == 0)
            return Array.Empty<IDomainEvent>();

        var copy = _domainEvents.ToArray();
        _domainEvents.Clear();
        return copy;
    }
}
