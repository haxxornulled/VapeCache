namespace VapeCache.Abstractions.Caching;

/// <summary>
/// Untyped cache key for general removal operations.
/// </summary>
public readonly record struct CacheKey(string Value);

/// <summary>
/// Strongly-typed cache key that carries the type of the cached value.
/// This enables compile-time type safety and eliminates the need to specify
/// the type parameter at call sites.
/// </summary>
/// <typeparam name="T">The type of value associated with this cache key.</typeparam>
public readonly record struct CacheKey<T>(string Value)
{
    /// <summary>
    /// Creates a typed cache key from a string value.
    /// </summary>
    public static CacheKey<T> From(string value) => new(value);

    /// <summary>
    /// Implicitly converts a typed key to an untyped key for removal operations.
    /// </summary>
    public static implicit operator CacheKey(CacheKey<T> typed) => new(typed.Value);
}
