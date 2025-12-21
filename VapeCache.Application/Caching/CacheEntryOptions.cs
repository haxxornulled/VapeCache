namespace VapeCache.Application.Caching;

public readonly record struct CacheEntryOptions(TimeSpan? Ttl = null);

