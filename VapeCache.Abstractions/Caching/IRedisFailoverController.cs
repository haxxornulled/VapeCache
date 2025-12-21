namespace VapeCache.Abstractions.Caching;

public interface IRedisFailoverController
{
    bool IsForcedOpen { get; }
    string? Reason { get; }

    void ForceOpen(string reason);
    void ClearForcedOpen();
}

