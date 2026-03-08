namespace VapeCache.Abstractions.Caching;

/// <summary>
/// Represents the struct.
/// </summary>
public readonly record struct CacheEntryOptions(
    TimeSpan? Ttl = null,
    CacheIntent? Intent = null);
