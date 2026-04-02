namespace VapeCache.Extensions.DistributedCache;

/// <summary>
/// Options for the VapeCache <see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/> adapter.
/// </summary>
public sealed class VapeCacheDistributedCacheOptions
{
    /// <summary>
    /// Optional prefix applied to all keys stored through the adapter.
    /// Use this when the adapter should coexist with native VapeCache callers in the same backend.
    /// </summary>
    public string KeyPrefix { get; set; } = string.Empty;
}
