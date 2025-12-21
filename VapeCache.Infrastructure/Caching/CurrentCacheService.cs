using VapeCache.Abstractions.Caching;

namespace VapeCache.Infrastructure.Caching;

internal sealed class CurrentCacheService : ICurrentCacheService
{
    private string _current = "redis";

    public string CurrentName => Volatile.Read(ref _current);

    public void SetCurrent(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        Volatile.Write(ref _current, name);
    }
}
