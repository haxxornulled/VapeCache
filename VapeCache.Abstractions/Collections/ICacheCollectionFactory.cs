namespace VapeCache.Abstractions.Collections;

/// <summary>
/// Factory for creating typed Redis collection wrappers.
/// Provides a clean API for working with Redis data structures.
/// </summary>
public interface ICacheCollectionFactory
{
    /// <summary>Create a typed LIST wrapper for the given key</summary>
    ICacheList<T> List<T>(string key);

    /// <summary>Create a typed SET wrapper for the given key</summary>
    ICacheSet<T> Set<T>(string key);

    /// <summary>Create a typed HASH wrapper for the given key</summary>
    ICacheHash<T> Hash<T>(string key);

    /// <summary>Create a typed SORTED SET wrapper for the given key</summary>
    ICacheSortedSet<T> SortedSet<T>(string key);
}
