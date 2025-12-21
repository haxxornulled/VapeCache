namespace VapeCache.Application.Caching;

public sealed record CacheStampedeOptions
{
    public bool Enabled { get; init; } = true;
    public int MaxKeys { get; init; } = 100_000;
}

