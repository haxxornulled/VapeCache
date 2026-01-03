using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Collections;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Infrastructure.Collections;

/// <summary>
/// Factory for creating typed Redis collection wrappers.
/// </summary>
internal sealed class CacheCollectionFactory : ICacheCollectionFactory
{
    private readonly IRedisCommandExecutor _executor;
    private readonly ICacheCodecProvider _codecProvider;

    public CacheCollectionFactory(IRedisCommandExecutor executor, ICacheCodecProvider codecProvider)
    {
        _executor = executor;
        _codecProvider = codecProvider;
    }

    public ICacheList<T> List<T>(string key)
    {
        var codec = _codecProvider.Get<T>();
        return new CacheList<T>(key, _executor, codec);
    }

    public ICacheSet<T> Set<T>(string key)
    {
        var codec = _codecProvider.Get<T>();
        return new CacheSet<T>(key, _executor, codec);
    }

    public ICacheHash<T> Hash<T>(string key)
    {
        var codec = _codecProvider.Get<T>();
        return new CacheHash<T>(key, _executor, codec);
    }

    public ICacheSortedSet<T> SortedSet<T>(string key)
    {
        var codec = _codecProvider.Get<T>();
        return new CacheSortedSet<T>(key, _executor, codec);
    }
}
