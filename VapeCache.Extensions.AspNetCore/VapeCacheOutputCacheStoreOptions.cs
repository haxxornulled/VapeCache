namespace VapeCache.Extensions.AspNetCore;

/// <summary>
/// Configuration options for the VapeCache-backed ASP.NET Core output-cache store.
/// </summary>
public sealed class VapeCacheOutputCacheStoreOptions
{
    /// <summary>
    /// Prefix used for all output-cache keys written by this store.
    /// </summary>
    public string KeyPrefix { get; set; } = "vapecache:output";

    /// <summary>
    /// Default TTL used when the middleware requests a non-positive cache duration.
    /// </summary>
    public TimeSpan DefaultTtl { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Enables in-memory tag indexing to support EvictByTag operations.
    /// </summary>
    public bool EnableTagIndexing { get; set; } = true;
}
