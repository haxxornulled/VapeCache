using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Diagnostics;

namespace VapeCache.UI.Features.CacheWorkbench;

public sealed class CacheWorkbenchOrchestrator(
    IVapeCache cache,
    ICacheBackendState backendState,
    ICacheStats cacheStats,
    IRedisCircuitBreakerState breakerState,
    IRedisFailoverController failoverController)
{
    public ValueTask<CacheWorkbenchStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var snapshot = cacheStats.Snapshot;
        var backend = backendState.EffectiveBackend;

        return ValueTask.FromResult(new CacheWorkbenchStatus(
            backend,
            breakerState.IsOpen,
            breakerState.ConsecutiveFailures,
            breakerState.OpenRemaining,
            failoverController.IsForcedOpen,
            failoverController.Reason,
            snapshot.GetCalls,
            snapshot.Hits,
            snapshot.Misses,
            snapshot.SetCalls,
            snapshot.RemoveCalls,
            snapshot.FallbackToMemory));
    }

    public async ValueTask<CacheWriteResult> SetStringAsync(
        string key,
        string value,
        int ttlSeconds,
        CancellationToken ct = default)
    {
        var normalizedKey = NormalizeKey(key);
        var options = ttlSeconds > 0
            ? new CacheEntryOptions(TimeSpan.FromSeconds(ttlSeconds))
            : new CacheEntryOptions(null);

        await cache.SetAsync(CacheKey<string>.From(normalizedKey), value, options, ct).ConfigureAwait(false);
        var stored = await cache.GetAsync(CacheKey<string>.From(normalizedKey), ct).ConfigureAwait(false);
        return new CacheWriteResult(normalizedKey, stored ?? string.Empty, options.Ttl);
    }

    public async ValueTask<CacheReadResult> ReadStringAsync(string key, CancellationToken ct = default)
    {
        var normalizedKey = NormalizeKey(key);
        var value = await cache.GetAsync(CacheKey<string>.From(normalizedKey), ct).ConfigureAwait(false);
        return new CacheReadResult(normalizedKey, value, value is not null);
    }

    public ValueTask<bool> RemoveAsync(string key, CancellationToken ct = default) =>
        cache.RemoveAsync(new CacheKey(NormalizeKey(key)), ct);

    public ValueTask ForceBreakerOpenAsync(string reason, CancellationToken ct = default)
    {
        var normalizedReason = string.IsNullOrWhiteSpace(reason)
            ? "manual-force-open"
            : reason.Trim();
        failoverController.ForceOpen(normalizedReason);
        return ValueTask.CompletedTask;
    }

    public ValueTask ClearBreakerForceOpenAsync(CancellationToken ct = default)
    {
        failoverController.ClearForcedOpen();
        return ValueTask.CompletedTask;
    }

    private static string NormalizeKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Cache key must not be empty.", nameof(key));

        return key.Trim();
    }
}

public sealed record CacheWorkbenchStatus(
    BackendType Backend,
    bool BreakerOpen,
    int ConsecutiveFailures,
    TimeSpan? BreakerOpenRemaining,
    bool BreakerForcedOpen,
    string? BreakerReason,
    long GetCalls,
    long Hits,
    long Misses,
    long SetCalls,
    long RemoveCalls,
    long FallbackToMemory);

public sealed record CacheWriteResult(string Key, string Value, TimeSpan? Ttl);

public sealed record CacheReadResult(string Key, string? Value, bool Found);
