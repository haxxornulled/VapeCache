namespace VapeCache.Application.Caching;

public interface ICurrentCacheService
{
    string CurrentName { get; }
    void SetCurrent(string name);
}

