using VapeCache.Abstractions.Diagnostics;

namespace VapeCache.Abstractions.Caching;

/// <summary>
/// Represents the cache intent kind.
/// </summary>
public enum CacheIntentKind
{
    /// <summary>
    /// Specifies unspecified.
    /// </summary>
    Unspecified = 0,
    /// <summary>
    /// Specifies read through.
    /// </summary>
    ReadThrough = 1,
    /// <summary>
    /// Specifies query result.
    /// </summary>
    QueryResult = 2,
    /// <summary>
    /// Specifies session state.
    /// </summary>
    SessionState = 3,
    /// <summary>
    /// Specifies dempotency.
    /// </summary>
    Idempotency = 4,
    /// <summary>
    /// Specifies rate limit.
    /// </summary>
    RateLimit = 5,
    /// <summary>
    /// Specifies feature flag.
    /// </summary>
    FeatureFlag = 6,
    /// <summary>
    /// Specifies computed view.
    /// </summary>
    ComputedView = 7,
    /// <summary>
    /// Specifies preload.
    /// </summary>
    Preload = 8
}

/// <summary>
/// Represents the cache intent.
/// </summary>
public sealed record CacheIntent
{
    public CacheIntent(
        CacheIntentKind Kind,
        string? Reason = null,
        string? Owner = null,
        string[]? Tags = null)
    {
        this.Kind = Kind;
        this.Reason = Reason;
        this.Owner = Owner;
        this.Tags = Tags;
    }

    public CacheIntentKind Kind { get; init; }
    public string? Reason { get; init; }
    public string? Owner { get; init; }
    public string[]? Tags { get; init; }
}

/// <summary>
/// Represents the cache intent entry.
/// </summary>
public sealed record CacheIntentEntry
{
    public CacheIntentEntry(
        string Key,
        BackendType Backend,
        CacheIntent Intent,
        DateTimeOffset RecordedAtUtc,
        DateTimeOffset? ExpiresAtUtc,
        int PayloadBytes)
    {
        this.Key = Key;
        this.Backend = Backend;
        this.Intent = Intent;
        this.RecordedAtUtc = RecordedAtUtc;
        this.ExpiresAtUtc = ExpiresAtUtc;
        this.PayloadBytes = PayloadBytes;
    }

    public string Key { get; init; }
    public BackendType Backend { get; init; }
    public CacheIntent Intent { get; init; }
    public DateTimeOffset RecordedAtUtc { get; init; }
    public DateTimeOffset? ExpiresAtUtc { get; init; }
    public int PayloadBytes { get; init; }
}

/// <summary>
/// Defines the cache intent registry contract.
/// </summary>
public interface ICacheIntentRegistry
{
    /// <summary>
    /// Executes record set.
    /// </summary>
    void RecordSet(string key, BackendType backend, in CacheEntryOptions options, int payloadBytes);
    /// <summary>
    /// Executes record remove.
    /// </summary>
    void RecordRemove(string key);
    /// <summary>
    /// Executes try get.
    /// </summary>
    bool TryGet(string key, out CacheIntentEntry? entry);
    /// <summary>
    /// Executes get recent.
    /// </summary>
    IReadOnlyList<CacheIntentEntry> GetRecent(int maxCount);
}
