using VapeCache.Abstractions.Caching;

namespace VapeCache.Infrastructure.Caching;

/// <summary>
/// High-level, type-safe cache client that wraps ICacheService with codec-based serialization.
/// This is the primary implementation of the ergonomic caching API.
/// </summary>
public sealed class VapeCacheClient : IVapeCache
{
    private readonly ICacheService _inner;
    private readonly ICacheCodecProvider _codecs;
    private readonly ICacheTagService? _tags;

    /// <summary>
    /// Executes vape cache client.
    /// </summary>
    public VapeCacheClient(ICacheService inner, ICacheCodecProvider codecs)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _codecs = codecs ?? throw new ArgumentNullException(nameof(codecs));
        _tags = inner as ICacheTagService;
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public ICacheRegion Region(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Region name cannot be null or whitespace.", nameof(name));

        return new CacheRegion(name, this);
    }

    /// <summary>
    /// Provides member behavior.
    /// </summary>
    public async ValueTask<T?> GetAsync<T>(CacheKey<T> key, CancellationToken ct = default)
    {
        var codec = _codecs.Get<T>();
        var result = await _inner.GetAsync(key.Value, codec.Deserialize, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Provides member behavior.
    /// </summary>
    public ValueTask SetAsync<T>(CacheKey<T> key, T value, CacheEntryOptions options = default, CancellationToken ct = default)
    {
        var codec = _codecs.Get<T>();
        return _inner.SetAsync(key.Value, value, codec.Serialize, options, ct);
    }

    /// <summary>
    /// Provides member behavior.
    /// </summary>
    public async ValueTask<T> GetOrCreateAsync<T>(
        CacheKey<T> key,
        Func<CancellationToken, ValueTask<T>> factory,
        CacheEntryOptions options = default,
        CancellationToken ct = default)
    {
        var codec = _codecs.Get<T>();
        var result = await _inner.GetOrSetAsync(
            key.Value,
            factory,
            codec.Serialize,
            codec.Deserialize,
            options,
            ct).ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// Removes value.
    /// </summary>
    public ValueTask<bool> RemoveAsync(CacheKey key, CancellationToken ct = default)
        => _inner.RemoveAsync(key.Value, ct);

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask<long> InvalidateTagAsync(string tag, CancellationToken ct = default)
        => RequireTagService().InvalidateTagAsync(tag, ct);

    /// <summary>
    /// Gets value.
    /// </summary>
    public ValueTask<long> GetTagVersionAsync(string tag, CancellationToken ct = default)
        => RequireTagService().GetTagVersionAsync(tag, ct);

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask<long> InvalidateZoneAsync(string zone, CancellationToken ct = default)
        => RequireTagService().InvalidateZoneAsync(zone, ct);

    /// <summary>
    /// Gets value.
    /// </summary>
    public ValueTask<long> GetZoneVersionAsync(string zone, CancellationToken ct = default)
        => RequireTagService().GetZoneVersionAsync(zone, ct);

    private ICacheTagService RequireTagService()
        => _tags ?? throw new NotSupportedException(
            "Tag and zone invalidation requires an ICacheTagService backend (HybridCacheService via AddVapecacheCaching).");

    /// <summary>
    /// Internal cache region implementation that automatically prefixes keys.
    /// </summary>
    private sealed class CacheRegion : ICacheRegion
    {
        private readonly string _name;
        private readonly VapeCacheClient _cache;

        public CacheRegion(string name, VapeCacheClient cache)
        {
            _name = name;
            _cache = cache;
        }

        public string Name => _name;

        public CacheKey<T> Key<T>(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Key ID cannot be null or whitespace.", nameof(id));

            return CacheKey<T>.From($"{_name}:{id}");
        }

        public ValueTask<T> GetOrCreateAsync<T>(
            string id,
            Func<CancellationToken, ValueTask<T>> factory,
            CacheEntryOptions options = default,
            CancellationToken ct = default)
            => _cache.GetOrCreateAsync(Key<T>(id), factory, options, ct);

        public ValueTask<T?> GetAsync<T>(string id, CancellationToken ct = default)
            => _cache.GetAsync(Key<T>(id), ct);

        public ValueTask SetAsync<T>(string id, T value, CacheEntryOptions options = default, CancellationToken ct = default)
            => _cache.SetAsync(Key<T>(id), value, options, ct);

        /// <summary>
        /// Removes value.
        /// </summary>
        public ValueTask<bool> RemoveAsync(string id, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Key ID cannot be null or whitespace.", nameof(id));

            return _cache.RemoveAsync(new CacheKey($"{_name}:{id}"), ct);
        }
    }
}
