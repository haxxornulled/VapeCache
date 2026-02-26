using System.Collections.Concurrent;
using VapeCache.Abstractions.Caching;

namespace VapeCache.Infrastructure.Caching;

internal sealed class CacheIntentRegistry : ICacheIntentRegistry
{
    private readonly ConcurrentDictionary<string, CacheIntentEntry> _entries = new(StringComparer.Ordinal);

    public void RecordSet(string key, string backend, in CacheEntryOptions options, int payloadBytes)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        var intent = options.Intent ?? new CacheIntent(CacheIntentKind.Unspecified, "unspecified");
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? expires = options.Ttl is null ? null : now.Add(options.Ttl.Value);

        _entries[key] = new CacheIntentEntry(
            Key: key,
            Backend: backend,
            Intent: intent,
            RecordedAtUtc: now,
            ExpiresAtUtc: expires,
            PayloadBytes: payloadBytes);
    }

    public void RecordRemove(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;
        _entries.TryRemove(key, out _);
    }

    public bool TryGet(string key, out CacheIntentEntry? entry)
    {
        if (_entries.TryGetValue(key, out var found))
        {
            if (IsExpired(found))
            {
                _entries.TryRemove(key, out _);
                entry = null;
                return false;
            }

            entry = found;
            return true;
        }

        entry = null;
        return false;
    }

    public IReadOnlyList<CacheIntentEntry> GetRecent(int maxCount)
    {
        var count = Math.Max(1, maxCount);
        var pq = new PriorityQueue<CacheIntentEntry, long>();

        foreach (var kvp in _entries)
        {
            var item = kvp.Value;
            if (IsExpired(item))
            {
                _entries.TryRemove(kvp.Key, out _);
                continue;
            }

            var priority = item.RecordedAtUtc.UtcTicks;
            if (pq.Count < count)
            {
                pq.Enqueue(item, priority);
                continue;
            }

            if (pq.TryPeek(out _, out var minPriority) && priority > minPriority)
            {
                pq.Dequeue();
                pq.Enqueue(item, priority);
            }
        }

        var result = new List<CacheIntentEntry>(pq.Count);
        while (pq.Count > 0)
            result.Add(pq.Dequeue());
        result.Reverse();
        return result;
    }

    private static bool IsExpired(in CacheIntentEntry entry)
        => entry.ExpiresAtUtc is { } expires && expires <= DateTimeOffset.UtcNow;
}
