namespace VapeCache.Core.Domain.Primitives;

public interface IDomainEvent
{
    DateTimeOffset OccurredOnUtc { get; }
}
