using VapeCache.Abstractions.Diagnostics;

namespace VapeCache.Abstractions.Caching;

public enum CacheIntentKind
{
    Unspecified = 0,
    ReadThrough = 1,
    QueryResult = 2,
    SessionState = 3,
    Idempotency = 4,
    RateLimit = 5,
    FeatureFlag = 6,
    ComputedView = 7,
    Preload = 8
}

public sealed record CacheIntent(
    CacheIntentKind Kind,
    string? Reason = null,
    string? Owner = null,
    string[]? Tags = null);

public sealed record CacheIntentEntry(
    string Key,
    BackendType Backend,
    CacheIntent Intent,
    DateTimeOffset RecordedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    int PayloadBytes);

public interface ICacheIntentRegistry
{
    void RecordSet(string key, BackendType backend, in CacheEntryOptions options, int payloadBytes);
    void RecordRemove(string key);
    bool TryGet(string key, out CacheIntentEntry? entry);
    IReadOnlyList<CacheIntentEntry> GetRecent(int maxCount);
}
