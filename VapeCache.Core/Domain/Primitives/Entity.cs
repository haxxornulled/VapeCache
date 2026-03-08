namespace VapeCache.Core.Domain.Primitives;

/// <summary>
/// Base entity primitive identified by a stable typed identifier.
/// </summary>
public abstract class Entity<TId> : IEquatable<Entity<TId>>
    where TId : notnull
{
    /// <summary>
    /// Gets the entity identifier.
    /// </summary>
    public TId Id { get; }

    /// <summary>
    /// Initializes the entity with an identifier.
    /// </summary>
    protected Entity(TId id)
    {
        Id = id;
    }

    /// <summary>
    /// Determines equality using concrete type and identifier value.
    /// </summary>
    public bool Equals(Entity<TId>? other)
    {
        if (other is null)
            return false;
        if (ReferenceEquals(this, other))
            return true;
        if (other.GetType() != GetType())
            return false;

        return EqualityComparer<TId>.Default.Equals(Id, other.Id);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Entity<TId> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}
