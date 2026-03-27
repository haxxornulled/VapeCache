namespace VapeCache.Abstractions.Caching;

/// <summary>
/// Represents the struct.
/// </summary>
public readonly record struct CacheEntryOptions
{
    public CacheEntryOptions(TimeSpan? Ttl = null, CacheIntent? Intent = null)
    {
        this.Ttl = Ttl;
        this.Intent = Intent;
    }

    public TimeSpan? Ttl { get; init; }
    public CacheIntent? Intent { get; init; }
}
