using System.Collections.Concurrent;
using System.Diagnostics;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Infrastructure.Connections;

/// <summary>
/// In-memory implementation of IRedisCommandExecutor for fallback scenarios.
/// Provides the same data structure operations as Redis (LIST, SET, HASH) but stored in memory.
/// Thread-safe and supports TTL expiration.
/// </summary>
internal sealed class InMemoryCommandExecutor : IRedisCommandExecutor
{
    private readonly ConcurrentDictionary<string, CacheEntry> _store = new();
    private readonly Timer _expirationTimer;
    private int _cleanupOffset = 0;

    public InMemoryCommandExecutor()
    {
        // Clean up expired entries every 60 seconds
        _expirationTimer = new Timer(CleanupExpiredEntries, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }

    private void CleanupExpiredEntries(object? state)
    {
        var now = DateTimeOffset.UtcNow;
        var processedCount = 0;
        const int MaxProcessedPerRun = 1000;

        var entries = _store.ToArray();
        if (entries.Length == 0) return;

        var startOffset = Volatile.Read(ref _cleanupOffset);

        for (int i = 0; i < entries.Length && processedCount < MaxProcessedPerRun; i++)
        {
            var idx = (startOffset + i) % entries.Length;
            var kvp = entries[idx];

            if (kvp.Value.ExpiresAt.HasValue && kvp.Value.ExpiresAt <= now)
            {
                _store.TryRemove(kvp.Key, out _);
            }
            processedCount++;
        }

        var nextOffset = (startOffset + processedCount) % Math.Max(1, entries.Length);
        Volatile.Write(ref _cleanupOffset, nextOffset);
    }

    private bool IsExpired(CacheEntry entry)
    {
        return entry.ExpiresAt.HasValue && entry.ExpiresAt <= DateTimeOffset.UtcNow;
    }

    // ========== String Commands ==========

    public ValueTask<byte[]?> GetAsync(string key, CancellationToken ct)
    {
        if (_store.TryGetValue(key, out var entry) && !IsExpired(entry) && entry.Type == EntryType.String)
            return ValueTask.FromResult<byte[]?>(entry.StringValue);
        return ValueTask.FromResult<byte[]?>(null);
    }

    public ValueTask<byte[]?> GetExAsync(string key, TimeSpan? ttl, CancellationToken ct)
    {
        if (_store.TryGetValue(key, out var entry) && !IsExpired(entry) && entry.Type == EntryType.String)
        {
            // Refresh TTL if specified
            if (ttl.HasValue)
                entry.ExpiresAt = DateTimeOffset.UtcNow.Add(ttl.Value);
            return ValueTask.FromResult<byte[]?>(entry.StringValue);
        }
        return ValueTask.FromResult<byte[]?>(null);
    }

    public ValueTask<byte[]?[]> MGetAsync(string[] keys, CancellationToken ct)
    {
        var result = new byte[]?[keys.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            if (_store.TryGetValue(keys[i], out var entry) && !IsExpired(entry) && entry.Type == EntryType.String)
                result[i] = entry.StringValue;
        }
        return ValueTask.FromResult(result);
    }

    public ValueTask<bool> SetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan? ttl, CancellationToken ct)
    {
        var entry = new CacheEntry
        {
            Type = EntryType.String,
            StringValue = value.ToArray(),
            ExpiresAt = ttl.HasValue ? DateTimeOffset.UtcNow.Add(ttl.Value) : null
        };
        _store[key] = entry;
        return ValueTask.FromResult(true);
    }

    public ValueTask<bool> MSetAsync((string Key, ReadOnlyMemory<byte> Value)[] items, CancellationToken ct)
    {
        foreach (var (key, value) in items)
        {
            var entry = new CacheEntry
            {
                Type = EntryType.String,
                StringValue = value.ToArray(),
                ExpiresAt = null
            };
            _store[key] = entry;
        }
        return ValueTask.FromResult(true);
    }

    public ValueTask<bool> DeleteAsync(string key, CancellationToken ct)
    {
        return ValueTask.FromResult(_store.TryRemove(key, out _));
    }

    public ValueTask<long> TtlSecondsAsync(string key, CancellationToken ct)
    {
        if (_store.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAt.HasValue)
            {
                var ttl = (long)(entry.ExpiresAt.Value - DateTimeOffset.UtcNow).TotalSeconds;
                return ValueTask.FromResult(ttl > 0 ? ttl : -2L); // -2 = key expired
            }
            return ValueTask.FromResult(-1L); // -1 = no expiration
        }
        return ValueTask.FromResult(-2L); // -2 = key doesn't exist
    }

    public ValueTask<long> PTtlMillisecondsAsync(string key, CancellationToken ct)
    {
        if (_store.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAt.HasValue)
            {
                var ttl = (long)(entry.ExpiresAt.Value - DateTimeOffset.UtcNow).TotalMilliseconds;
                return ValueTask.FromResult(ttl > 0 ? ttl : -2L);
            }
            return ValueTask.FromResult(-1L);
        }
        return ValueTask.FromResult(-2L);
    }

    public ValueTask<long> UnlinkAsync(string key, CancellationToken ct)
    {
        return ValueTask.FromResult(_store.TryRemove(key, out _) ? 1L : 0L);
    }

    public ValueTask<RedisValueLease> GetLeaseAsync(string key, CancellationToken ct)
    {
        if (_store.TryGetValue(key, out var entry) && !IsExpired(entry) && entry.Type == EntryType.String && entry.StringValue != null)
        {
            // Return non-pooled lease (data is already in memory, no need for ArrayPool)
            return ValueTask.FromResult(new RedisValueLease(entry.StringValue, entry.StringValue.Length, pooled: false));
        }
        return ValueTask.FromResult(default(RedisValueLease));
    }

    public ValueTask<RedisValueLease> GetExLeaseAsync(string key, TimeSpan? ttl, CancellationToken ct)
    {
        if (_store.TryGetValue(key, out var entry) && !IsExpired(entry) && entry.Type == EntryType.String && entry.StringValue != null)
        {
            if (ttl.HasValue)
                entry.ExpiresAt = DateTimeOffset.UtcNow.Add(ttl.Value);

            return ValueTask.FromResult(new RedisValueLease(entry.StringValue, entry.StringValue.Length, pooled: false));
        }
        return ValueTask.FromResult(default(RedisValueLease));
    }

    // ========== Hash Commands ==========

    public ValueTask<long> HSetAsync(string key, string field, ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        var entry = _store.GetOrAdd(key, _ => new CacheEntry { Type = EntryType.Hash, HashValue = new() });
        if (IsExpired(entry)) return ValueTask.FromResult(0L);

        if (entry.Type != EntryType.Hash)
            throw new InvalidOperationException($"Key '{key}' is not a hash");

        var isNew = !entry.HashValue!.ContainsKey(field);
        entry.HashValue[field] = value.ToArray();
        return ValueTask.FromResult(isNew ? 1L : 0L);
    }

    public ValueTask<byte[]?> HGetAsync(string key, string field, CancellationToken ct)
    {
        if (_store.TryGetValue(key, out var entry) && !IsExpired(entry) && entry.Type == EntryType.Hash)
        {
            entry.HashValue!.TryGetValue(field, out var value);
            return ValueTask.FromResult(value);
        }
        return ValueTask.FromResult<byte[]?>(null);
    }

    public ValueTask<byte[]?[]> HMGetAsync(string key, string[] fields, CancellationToken ct)
    {
        var result = new byte[]?[fields.Length];
        if (_store.TryGetValue(key, out var entry) && !IsExpired(entry) && entry.Type == EntryType.Hash)
        {
            for (var i = 0; i < fields.Length; i++)
            {
                entry.HashValue!.TryGetValue(fields[i], out result[i]);
            }
        }
        return ValueTask.FromResult(result);
    }

    public ValueTask<RedisValueLease> HGetLeaseAsync(string key, string field, CancellationToken ct)
    {
        if (_store.TryGetValue(key, out var entry) && !IsExpired(entry) && entry.Type == EntryType.Hash)
        {
            if (entry.HashValue!.TryGetValue(field, out var value))
            {
                return ValueTask.FromResult(new RedisValueLease(value, value.Length, pooled: false));
            }
        }
        return ValueTask.FromResult(default(RedisValueLease));
    }

    // ========== List Commands ==========

    public ValueTask<long> LPushAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        var entry = _store.GetOrAdd(key, _ => new CacheEntry { Type = EntryType.List, ListValue = new() });
        if (IsExpired(entry)) return ValueTask.FromResult(0L);

        if (entry.Type != EntryType.List)
            throw new InvalidOperationException($"Key '{key}' is not a list");

        entry.ListValue!.AddFirst(value.ToArray());
        return ValueTask.FromResult((long)entry.ListValue.Count);
    }

    public ValueTask<long> RPushAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        var entry = _store.GetOrAdd(key, _ => new CacheEntry { Type = EntryType.List, ListValue = new() });
        if (IsExpired(entry)) return ValueTask.FromResult(0L);

        if (entry.Type != EntryType.List)
            throw new InvalidOperationException($"Key '{key}' is not a list");

        entry.ListValue!.AddLast(value.ToArray());
        return ValueTask.FromResult((long)entry.ListValue.Count);
    }

    public ValueTask<byte[]?> LPopAsync(string key, CancellationToken ct)
    {
        if (_store.TryGetValue(key, out var entry) && !IsExpired(entry) && entry.Type == EntryType.List && entry.ListValue!.Count > 0)
        {
            var value = entry.ListValue.First!.Value;
            entry.ListValue.RemoveFirst();
            if (entry.ListValue.Count == 0)
                _store.TryRemove(key, out _); // Remove empty list
            return ValueTask.FromResult<byte[]?>(value);
        }
        return ValueTask.FromResult<byte[]?>(null);
    }

    public ValueTask<byte[]?> RPopAsync(string key, CancellationToken ct)
    {
        if (_store.TryGetValue(key, out var entry) && !IsExpired(entry) && entry.Type == EntryType.List && entry.ListValue!.Count > 0)
        {
            var value = entry.ListValue.Last!.Value;
            entry.ListValue.RemoveLast();
            if (entry.ListValue.Count == 0)
                _store.TryRemove(key, out _);
            return ValueTask.FromResult<byte[]?>(value);
        }
        return ValueTask.FromResult<byte[]?>(null);
    }

    public ValueTask<byte[]?[]> LRangeAsync(string key, long start, long stop, CancellationToken ct)
    {
        if (_store.TryGetValue(key, out var entry) && !IsExpired(entry) && entry.Type == EntryType.List)
        {
            var list = entry.ListValue!;
            var count = list.Count;

            // Handle negative indices
            if (start < 0) start = count + start;
            if (stop < 0) stop = count + stop;

            // Clamp to valid range
            start = Math.Max(0, Math.Min(start, count - 1));
            stop = Math.Max(0, Math.Min(stop, count - 1));

            if (start > stop) return ValueTask.FromResult(Array.Empty<byte[]?>());

            var result = new byte[]?[(int)(stop - start + 1)];
            var index = 0;
            var currentIndex = 0;
            foreach (var item in list)
            {
                if (currentIndex >= start && currentIndex <= stop)
                    result[index++] = item;
                if (currentIndex > stop) break;
                currentIndex++;
            }
            return ValueTask.FromResult(result);
        }
        return ValueTask.FromResult(Array.Empty<byte[]?>());
    }

    public ValueTask<long> LLenAsync(string key, CancellationToken ct)
    {
        if (_store.TryGetValue(key, out var entry) && !IsExpired(entry) && entry.Type == EntryType.List)
            return ValueTask.FromResult((long)entry.ListValue!.Count);
        return ValueTask.FromResult(0L);
    }

    public ValueTask<RedisValueLease> LPopLeaseAsync(string key, CancellationToken ct)
    {
        if (_store.TryGetValue(key, out var entry) && !IsExpired(entry) && entry.Type == EntryType.List)
        {
            if (entry.ListValue!.Count > 0)
            {
                var value = entry.ListValue.First!.Value;
                entry.ListValue.RemoveFirst();
                return ValueTask.FromResult(new RedisValueLease(value, value.Length, pooled: false));
            }
        }
        return ValueTask.FromResult(default(RedisValueLease));
    }

    // ========== Set Commands ==========

    public ValueTask<long> SAddAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        var entry = _store.GetOrAdd(key, _ => new CacheEntry { Type = EntryType.Set, SetValue = new(ByteArrayComparer.Instance) });
        if (IsExpired(entry)) return ValueTask.FromResult(0L);

        if (entry.Type != EntryType.Set)
            throw new InvalidOperationException($"Key '{key}' is not a set");

        var added = entry.SetValue!.TryAdd(member.ToArray(), 0);
        return ValueTask.FromResult(added ? 1L : 0L);
    }

    public ValueTask<long> SRemAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        if (_store.TryGetValue(key, out var entry) && !IsExpired(entry) && entry.Type == EntryType.Set)
        {
            var removed = entry.SetValue!.TryRemove(member.ToArray(), out _);
            if (entry.SetValue.Count == 0)
                _store.TryRemove(key, out _);
            return ValueTask.FromResult(removed ? 1L : 0L);
        }
        return ValueTask.FromResult(0L);
    }

    public ValueTask<bool> SIsMemberAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        if (_store.TryGetValue(key, out var entry) && !IsExpired(entry) && entry.Type == EntryType.Set)
            return ValueTask.FromResult(entry.SetValue!.ContainsKey(member.ToArray()));
        return ValueTask.FromResult(false);
    }

    public ValueTask<byte[]?[]> SMembersAsync(string key, CancellationToken ct)
    {
        if (_store.TryGetValue(key, out var entry) && !IsExpired(entry) && entry.Type == EntryType.Set)
            return ValueTask.FromResult(entry.SetValue!.Keys.ToArray<byte[]?>());
        return ValueTask.FromResult(Array.Empty<byte[]?>());
    }

    public ValueTask<long> SCardAsync(string key, CancellationToken ct)
    {
        if (_store.TryGetValue(key, out var entry) && !IsExpired(entry) && entry.Type == EntryType.Set)
            return ValueTask.FromResult((long)entry.SetValue!.Count);
        return ValueTask.FromResult(0L);
    }

    // ========== Server Commands ==========

    public ValueTask<string> PingAsync(CancellationToken ct)
    {
        return ValueTask.FromResult("PONG");
    }

    public ValueTask<string[]> ModuleListAsync(CancellationToken ct)
    {
        // In-memory executor doesn't support modules
        return ValueTask.FromResult(Array.Empty<string>());
    }

    public ValueTask DisposeAsync()
    {
        _expirationTimer.Dispose();
        _store.Clear();
        return ValueTask.CompletedTask;
    }

    // ========== Internal Types ==========

    private sealed class CacheEntry
    {
        public EntryType Type { get; set; }
        public byte[]? StringValue { get; set; }
        public ConcurrentDictionary<string, byte[]>? HashValue { get; set; }
        public LinkedList<byte[]>? ListValue { get; set; }
        public ConcurrentDictionary<byte[], byte>? SetValue { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
    }

    private enum EntryType
    {
        String,
        Hash,
        List,
        Set
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y)
        {
            if (x == null || y == null) return x == y;
            return x.AsSpan().SequenceEqual(y.AsSpan());
        }

        public int GetHashCode(byte[] obj)
        {
            var hash = new HashCode();
            hash.AddBytes(obj);
            return hash.ToHashCode();
        }
    }
}
