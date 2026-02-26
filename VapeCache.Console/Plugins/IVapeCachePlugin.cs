using VapeCache.Abstractions.Caching;

namespace VapeCache.Console.Plugins;

/// <summary>
/// Plugin contract for extending VapeCache host behavior without modifying core library code.
/// </summary>
public interface IVapeCachePlugin
{
    string Name { get; }

    ValueTask ExecuteAsync(
        ICacheService cache,
        ICurrentCacheService current,
        CancellationToken cancellationToken);
}
