namespace VapeCache.Abstractions.Caching;

public readonly record struct CacheEntryOptions(
    TimeSpan? Ttl = null,
    CacheIntent? Intent = null);
