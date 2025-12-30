using System.Collections.Concurrent;

namespace VapeCache.Infrastructure.Caching;

internal sealed class CacheStatsRegistry
{
    private readonly ConcurrentDictionary<string, CacheStats> _stats = new(StringComparer.OrdinalIgnoreCase);

    public CacheStats GetOrCreate(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            name = CacheStatsNames.Unknown;

        return _stats.GetOrAdd(name, _ => new CacheStats());
    }

    public bool TryGet(string name, out CacheStats stats)
        => _stats.TryGetValue(name, out stats!);
}
