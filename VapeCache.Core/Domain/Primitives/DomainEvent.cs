namespace VapeCache.Core.Domain.Primitives;

public abstract record DomainEvent(DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    protected DomainEvent() : this(DateTimeOffset.UtcNow)
    {
    }
}
