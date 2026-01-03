using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Infrastructure.Connections;

/// <summary>
/// In-memory implementation of IRedisCommandExecutor for fallback scenarios.
/// Provides the same data structure operations as Redis (LIST, SET, HASH) but stored in memory.
/// Thread-safe and supports TTL expiration.
/// </summary>
internal sealed class InMemoryCommandExecutor : IRedisFallbackCommandExecutor
{
    private readonly ConcurrentDictionary<string, CacheEntry> _store = new();
    private readonly Timer _expirationTimer;
    private int _cleanupOffset = 0;

    public InMemoryCommandExecutor()
    {
        // Clean up expired entries every 60 seconds
        _expirationTimer = new Timer(CleanupExpiredEntries, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }

    public string Name => "memory";

    public IRedisBatch CreateBatch()
        => new RedisBatch(this);

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

    public bool TryGetAsync(string key, CancellationToken ct, out ValueTask<byte[]?> task)
    {
        task = GetAsync(key, ct);
        return true;
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

    public ValueTask<byte[]?> GetRangeAsync(string key, long start, long end, CancellationToken ct)
    {
        if (_store.TryGetValue(key, out var entry) && !IsExpired(entry) && entry.Type == EntryType.String && entry.StringValue is not null)
        {
            var value = entry.StringValue;
            if (value.Length == 0)
                return ValueTask.FromResult<byte[]?>(Array.Empty<byte>());

            if (start < 0) start = value.Length + start;
            if (end < 0) end = value.Length + end;

            if (start < 0) start = 0;
            if (end < 0) return ValueTask.FromResult<byte[]?>(Array.Empty<byte>());

            if (start >= value.Length) return ValueTask.FromResult<byte[]?>(Array.Empty<byte>());
            if (end >= value.Length) end = value.Length - 1;
            if (start > end) return ValueTask.FromResult<byte[]?>(Array.Empty<byte>());

            var length = (int)(end - start + 1);
            var slice = new byte[length];
            Buffer.BlockCopy(value, (int)start, slice, 0, length);
            return ValueTask.FromResult<byte[]?>(slice);
        }
        return ValueTask.FromResult<byte[]?>(null);
    }

    public ValueTask<byte[]?> JsonGetAsync(string key, string? path, CancellationToken ct)
    {
        return GetAsync(key, ct);
    }

    public ValueTask<RedisValueLease> JsonGetLeaseAsync(string key, string? path, CancellationToken ct)
    {
        return GetLeaseAsync(key, ct);
    }

    public bool TryJsonGetLeaseAsync(string key, string? path, CancellationToken ct, out ValueTask<RedisValueLease> task)
    {
        task = GetLeaseAsync(key, ct);
        return true;
    }

    public ValueTask<bool> JsonSetAsync(string key, string? path, ReadOnlyMemory<byte> json, CancellationToken ct)
    {
        return SetAsync(key, json, null, ct);
    }

    public ValueTask<bool> JsonSetLeaseAsync(string key, string? path, RedisValueLease json, CancellationToken ct)
    {
        return SetAsync(key, json.Memory, null, ct);
    }

    public async ValueTask<long> JsonDelAsync(string key, string? path, CancellationToken ct)
    {
        var removed = await DeleteAsync(key, ct).ConfigureAwait(false);
        return removed ? 1L : 0L;
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

    public bool TrySetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan? ttl, CancellationToken ct, out ValueTask<bool> task)
    {
        task = SetAsync(key, value, ttl, ct);
        return true;
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

    public ValueTask<bool> ExpireAsync(string key, TimeSpan ttl, CancellationToken ct)
    {
        if (!_store.TryGetValue(key, out var entry))
            return ValueTask.FromResult(false);

        if (IsExpired(entry))
        {
            _store.TryRemove(key, out _);
            return ValueTask.FromResult(false);
        }

        if (ttl <= TimeSpan.Zero)
        {
            _store.TryRemove(key, out _);
            return ValueTask.FromResult(true);
        }

        entry.ExpiresAt = DateTimeOffset.UtcNow.Add(ttl);
        return ValueTask.FromResult(true);
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

    public bool TryGetLeaseAsync(string key, CancellationToken ct, out ValueTask<RedisValueLease> task)
    {
        task = GetLeaseAsync(key, ct);
        return true;
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

    public bool TryGetExLeaseAsync(string key, TimeSpan? ttl, CancellationToken ct, out ValueTask<RedisValueLease> task)
    {
        task = GetExLeaseAsync(key, ttl, ct);
        return true;
    }

    // ========== Hash Commands ==========

    public ValueTask<long> HSetAsync(string key, string field, ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        var entry = _store.GetOrAdd(key, _ => new CacheEntry { Type = EntryType.Hash, HashValue = new() });
        lock (entry.Sync)
        {
            if (IsExpired(entry))
            {
                _store.TryRemove(key, out _);
                return ValueTask.FromResult(0L);
            }

            if (entry.Type != EntryType.Hash)
                throw new InvalidOperationException($"Key '{key}' is not a hash");

            var bytes = value.ToArray();
            if (entry.HashValue!.TryAdd(field, bytes))
                return ValueTask.FromResult(1L);

            entry.HashValue[field] = bytes;
            return ValueTask.FromResult(0L);
        }
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

    public bool TryHGetAsync(string key, string field, CancellationToken ct, out ValueTask<byte[]?> task)
    {
        task = HGetAsync(key, field, ct);
        return true;
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
        lock (entry.Sync)
        {
            if (IsExpired(entry))
            {
                _store.TryRemove(key, out _);
                return ValueTask.FromResult(0L);
            }

            if (entry.Type != EntryType.List)
                throw new InvalidOperationException($"Key '{key}' is not a list");

            entry.ListValue!.AddFirst(value.ToArray());
            return ValueTask.FromResult((long)entry.ListValue.Count);
        }
    }

    public ValueTask<long> RPushAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        var entry = _store.GetOrAdd(key, _ => new CacheEntry { Type = EntryType.List, ListValue = new() });
        lock (entry.Sync)
        {
            if (IsExpired(entry))
            {
                _store.TryRemove(key, out _);
                return ValueTask.FromResult(0L);
            }

            if (entry.Type != EntryType.List)
                throw new InvalidOperationException($"Key '{key}' is not a list");

            entry.ListValue!.AddLast(value.ToArray());
            return ValueTask.FromResult((long)entry.ListValue.Count);
        }
    }

    public ValueTask<byte[]?> LPopAsync(string key, CancellationToken ct)
    {
        if (_store.TryGetValue(key, out var entry))
        {
            lock (entry.Sync)
            {
                if (IsExpired(entry))
                {
                    _store.TryRemove(key, out _);
                    return ValueTask.FromResult<byte[]?>(null);
                }

                if (entry.Type == EntryType.List && entry.ListValue!.Count > 0)
                {
                    var value = entry.ListValue.First!.Value;
                    entry.ListValue.RemoveFirst();
                    if (entry.ListValue.Count == 0)
                        _store.TryRemove(key, out _); // Remove empty list
                    return ValueTask.FromResult<byte[]?>(value);
                }
            }
        }
        return ValueTask.FromResult<byte[]?>(null);
    }

    public ValueTask<byte[]?> LIndexAsync(string key, long index, CancellationToken ct)
    {
        if (_store.TryGetValue(key, out var entry))
        {
            lock (entry.Sync)
            {
                if (IsExpired(entry))
                {
                    _store.TryRemove(key, out _);
                    return ValueTask.FromResult<byte[]?>(null);
                }

                if (entry.Type != EntryType.List)
                    return ValueTask.FromResult<byte[]?>(null);

                var list = entry.ListValue!;
                var count = list.Count;
                if (count == 0) return ValueTask.FromResult<byte[]?>(null);

                if (index < 0) index = count + index;
                if (index < 0 || index >= count) return ValueTask.FromResult<byte[]?>(null);

                if (index <= count / 2)
                {
                    var node = list.First;
                    for (var i = 0; i < index; i++)
                        node = node!.Next;
                    return ValueTask.FromResult<byte[]?>(node!.Value);
                }

                var fromEnd = count - 1;
                var tail = list.Last;
                for (var i = fromEnd; i > index; i--)
                    tail = tail!.Previous;
                return ValueTask.FromResult<byte[]?>(tail!.Value);
            }
        }
        return ValueTask.FromResult<byte[]?>(null);
    }

    public bool TryGetExAsync(string key, TimeSpan? ttl, CancellationToken ct, out ValueTask<byte[]?> task)
    {
        task = GetExAsync(key, ttl, ct);
        return true;
    }

    public bool TryLPopAsync(string key, CancellationToken ct, out ValueTask<byte[]?> task)
    {
        task = LPopAsync(key, ct);
        return true;
    }

    public ValueTask<byte[]?> RPopAsync(string key, CancellationToken ct)
    {
        if (_store.TryGetValue(key, out var entry))
        {
            lock (entry.Sync)
            {
                if (IsExpired(entry))
                {
                    _store.TryRemove(key, out _);
                    return ValueTask.FromResult<byte[]?>(null);
                }

                if (entry.Type == EntryType.List && entry.ListValue!.Count > 0)
                {
                    var value = entry.ListValue.Last!.Value;
                    entry.ListValue.RemoveLast();
                    if (entry.ListValue.Count == 0)
                        _store.TryRemove(key, out _);
                    return ValueTask.FromResult<byte[]?>(value);
                }
            }
        }
        return ValueTask.FromResult<byte[]?>(null);
    }

    public bool TryRPopAsync(string key, CancellationToken ct, out ValueTask<byte[]?> task)
    {
        task = RPopAsync(key, ct);
        return true;
    }

    public ValueTask<byte[]?[]> LRangeAsync(string key, long start, long stop, CancellationToken ct)
    {
        if (_store.TryGetValue(key, out var entry))
        {
            lock (entry.Sync)
            {
                if (IsExpired(entry))
                {
                    _store.TryRemove(key, out _);
                    return ValueTask.FromResult(Array.Empty<byte[]?>());
                }

                if (entry.Type != EntryType.List)
                    return ValueTask.FromResult(Array.Empty<byte[]?>());

                var list = entry.ListValue!;
                var count = list.Count;
                if (count == 0)
                    return ValueTask.FromResult(Array.Empty<byte[]?>());

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
        }
        return ValueTask.FromResult(Array.Empty<byte[]?>());
    }

    public ValueTask<long> LLenAsync(string key, CancellationToken ct)
    {
        if (_store.TryGetValue(key, out var entry))
        {
            lock (entry.Sync)
            {
                if (IsExpired(entry))
                {
                    _store.TryRemove(key, out _);
                    return ValueTask.FromResult(0L);
                }

                if (entry.Type == EntryType.List)
                    return ValueTask.FromResult((long)entry.ListValue!.Count);
            }
        }
        return ValueTask.FromResult(0L);
    }

    public ValueTask<RedisValueLease> LPopLeaseAsync(string key, CancellationToken ct)
    {
        if (_store.TryGetValue(key, out var entry))
        {
            lock (entry.Sync)
            {
                if (IsExpired(entry))
                {
                    _store.TryRemove(key, out _);
                    return ValueTask.FromResult(default(RedisValueLease));
                }

                if (entry.Type == EntryType.List && entry.ListValue!.Count > 0)
                {
                    var value = entry.ListValue.First!.Value;
                    entry.ListValue.RemoveFirst();
                    return ValueTask.FromResult(new RedisValueLease(value, value.Length, pooled: false));
                }
            }
        }
        return ValueTask.FromResult(default(RedisValueLease));
    }

    public ValueTask<RedisValueLease> RPopLeaseAsync(string key, CancellationToken ct)
    {
        if (_store.TryGetValue(key, out var entry))
        {
            lock (entry.Sync)
            {
                if (IsExpired(entry))
                {
                    _store.TryRemove(key, out _);
                    return ValueTask.FromResult(default(RedisValueLease));
                }

                if (entry.Type == EntryType.List && entry.ListValue!.Count > 0)
                {
                    var value = entry.ListValue.Last!.Value;
                    entry.ListValue.RemoveLast();
                    if (entry.ListValue.Count == 0)
                        _store.TryRemove(key, out _);
                    return ValueTask.FromResult(new RedisValueLease(value, value.Length, pooled: false));
                }
            }
        }
        return ValueTask.FromResult(default(RedisValueLease));
    }

    public bool TryLPopLeaseAsync(string key, CancellationToken ct, out ValueTask<RedisValueLease> task)
    {
        task = LPopLeaseAsync(key, ct);
        return true;
    }

    public bool TryRPopLeaseAsync(string key, CancellationToken ct, out ValueTask<RedisValueLease> task)
    {
        task = RPopLeaseAsync(key, ct);
        return true;
    }

    // ========== Set Commands ==========

    public ValueTask<long> SAddAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        var entry = _store.GetOrAdd(key, _ => new CacheEntry { Type = EntryType.Set, SetValue = new(ByteArrayComparer.Instance) });
        lock (entry.Sync)
        {
            if (IsExpired(entry))
            {
                _store.TryRemove(key, out _);
                return ValueTask.FromResult(0L);
            }

            if (entry.Type != EntryType.Set)
                throw new InvalidOperationException($"Key '{key}' is not a set");

            var added = entry.SetValue!.TryAdd(member.ToArray(), 0);
            return ValueTask.FromResult(added ? 1L : 0L);
        }
    }

    public ValueTask<long> SRemAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        if (_store.TryGetValue(key, out var entry))
        {
            lock (entry.Sync)
            {
                if (IsExpired(entry))
                {
                    _store.TryRemove(key, out _);
                    return ValueTask.FromResult(0L);
                }

                if (entry.Type != EntryType.Set)
                    return ValueTask.FromResult(0L);

                var removed = entry.SetValue!.TryRemove(member.ToArray(), out _);
                if (removed && entry.SetValue.Count == 0)
                    _store.TryRemove(key, out _);
                return ValueTask.FromResult(removed ? 1L : 0L);
            }
        }
        return ValueTask.FromResult(0L);
    }

    public ValueTask<bool> SIsMemberAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        if (_store.TryGetValue(key, out var entry))
        {
            lock (entry.Sync)
            {
                if (IsExpired(entry))
                {
                    _store.TryRemove(key, out _);
                    return ValueTask.FromResult(false);
                }

                if (entry.Type != EntryType.Set)
                    return ValueTask.FromResult(false);

                return ValueTask.FromResult(entry.SetValue!.ContainsKey(member.ToArray()));
            }
        }
        return ValueTask.FromResult(false);
    }

    public bool TrySIsMemberAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct, out ValueTask<bool> task)
    {
        task = SIsMemberAsync(key, member, ct);
        return true;
    }

    public ValueTask<byte[]?[]> SMembersAsync(string key, CancellationToken ct)
    {
        if (_store.TryGetValue(key, out var entry))
        {
            lock (entry.Sync)
            {
                if (IsExpired(entry))
                {
                    _store.TryRemove(key, out _);
                    return ValueTask.FromResult(Array.Empty<byte[]?>());
                }

                if (entry.Type != EntryType.Set)
                    return ValueTask.FromResult(Array.Empty<byte[]?>());

                return ValueTask.FromResult(entry.SetValue!.Keys.ToArray<byte[]?>());
            }
        }
        return ValueTask.FromResult(Array.Empty<byte[]?>());
    }

    public ValueTask<long> SCardAsync(string key, CancellationToken ct)
    {
        if (_store.TryGetValue(key, out var entry))
        {
            lock (entry.Sync)
            {
                if (IsExpired(entry))
                {
                    _store.TryRemove(key, out _);
                    return ValueTask.FromResult(0L);
                }

                if (entry.Type != EntryType.Set)
                    return ValueTask.FromResult(0L);

                return ValueTask.FromResult((long)entry.SetValue!.Count);
            }
        }
        return ValueTask.FromResult(0L);
    }

    // ========== Sorted Set Commands ==========

    public ValueTask<long> ZAddAsync(string key, double score, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        var entry = _store.GetOrAdd(key, _ => new CacheEntry
        {
            Type = EntryType.SortedSet,
            SortedSetValue = new SortedSet<SortedSetEntry>(SortedSetEntryComparer.Instance),
            SortedSetScores = new Dictionary<byte[], double>(ByteArrayComparer.Instance)
        });

        lock (entry.Sync)
        {
            if (IsExpired(entry))
            {
                _store.TryRemove(key, out _);
                return ValueTask.FromResult(0L);
            }

            if (entry.Type != EntryType.SortedSet)
                throw new InvalidOperationException($"Key '{key}' is not a sorted set");

            var memberBytes = member.ToArray();
            if (entry.SortedSetScores!.TryGetValue(memberBytes, out var existingScore))
            {
                entry.SortedSetValue!.Remove(new SortedSetEntry(memberBytes, existingScore));
                entry.SortedSetScores[memberBytes] = score;
                entry.SortedSetValue.Add(new SortedSetEntry(memberBytes, score));
                return ValueTask.FromResult(0L);
            }

            entry.SortedSetScores[memberBytes] = score;
            entry.SortedSetValue!.Add(new SortedSetEntry(memberBytes, score));
            return ValueTask.FromResult(1L);
        }
    }

    public ValueTask<long> ZRemAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        if (!_store.TryGetValue(key, out var entry))
            return ValueTask.FromResult(0L);

        lock (entry.Sync)
        {
            if (IsExpired(entry))
            {
                _store.TryRemove(key, out _);
                return ValueTask.FromResult(0L);
            }

            if (entry.Type != EntryType.SortedSet)
                throw new InvalidOperationException($"Key '{key}' is not a sorted set");

            var memberBytes = member.ToArray();
            if (!entry.SortedSetScores!.TryGetValue(memberBytes, out var score))
                return ValueTask.FromResult(0L);

            entry.SortedSetScores.Remove(memberBytes);
            entry.SortedSetValue!.Remove(new SortedSetEntry(memberBytes, score));

            if (entry.SortedSetScores.Count == 0)
                _store.TryRemove(key, out _);

            return ValueTask.FromResult(1L);
        }
    }

    public ValueTask<long> ZCardAsync(string key, CancellationToken ct)
    {
        if (_store.TryGetValue(key, out var entry))
        {
            lock (entry.Sync)
            {
                if (IsExpired(entry))
                {
                    _store.TryRemove(key, out _);
                    return ValueTask.FromResult(0L);
                }

                if (entry.Type == EntryType.SortedSet)
                    return ValueTask.FromResult((long)entry.SortedSetScores!.Count);
            }
        }

        return ValueTask.FromResult(0L);
    }

    public ValueTask<double?> ZScoreAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        if (_store.TryGetValue(key, out var entry))
        {
            lock (entry.Sync)
            {
                if (IsExpired(entry))
                {
                    _store.TryRemove(key, out _);
                    return ValueTask.FromResult<double?>(null);
                }

                if (entry.Type != EntryType.SortedSet)
                    return ValueTask.FromResult<double?>(null);

                var memberBytes = member.ToArray();
                return entry.SortedSetScores!.TryGetValue(memberBytes, out var score)
                    ? ValueTask.FromResult<double?>(score)
                    : ValueTask.FromResult<double?>(null);
            }
        }

        return ValueTask.FromResult<double?>(null);
    }

    public ValueTask<long?> ZRankAsync(string key, ReadOnlyMemory<byte> member, bool descending, CancellationToken ct)
    {
        if (!_store.TryGetValue(key, out var entry))
            return ValueTask.FromResult<long?>(null);

        lock (entry.Sync)
        {
            if (IsExpired(entry))
            {
                _store.TryRemove(key, out _);
                return ValueTask.FromResult<long?>(null);
            }

            if (entry.Type != EntryType.SortedSet)
                return ValueTask.FromResult<long?>(null);

            var memberBytes = member.ToArray();
            if (!entry.SortedSetScores!.TryGetValue(memberBytes, out var score))
                return ValueTask.FromResult<long?>(null);

            var target = new SortedSetEntry(memberBytes, score);
            long index = 0;
            if (descending)
            {
                foreach (var item in entry.SortedSetValue!.Reverse())
                {
                    if (SortedSetEntryComparer.Instance.Compare(item, target) == 0)
                        return ValueTask.FromResult<long?>(index);
                    index++;
                }
            }
            else
            {
                foreach (var item in entry.SortedSetValue!)
                {
                    if (SortedSetEntryComparer.Instance.Compare(item, target) == 0)
                        return ValueTask.FromResult<long?>(index);
                    index++;
                }
            }
        }

        return ValueTask.FromResult<long?>(null);
    }

    public ValueTask<double> ZIncrByAsync(string key, double increment, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        var entry = _store.GetOrAdd(key, _ => new CacheEntry
        {
            Type = EntryType.SortedSet,
            SortedSetValue = new SortedSet<SortedSetEntry>(SortedSetEntryComparer.Instance),
            SortedSetScores = new Dictionary<byte[], double>(ByteArrayComparer.Instance)
        });

        lock (entry.Sync)
        {
            if (IsExpired(entry))
            {
                _store.TryRemove(key, out _);
                return ValueTask.FromResult(0d);
            }

            if (entry.Type != EntryType.SortedSet)
                throw new InvalidOperationException($"Key '{key}' is not a sorted set");

            var memberBytes = member.ToArray();
            var newScore = increment;
            if (entry.SortedSetScores!.TryGetValue(memberBytes, out var existingScore))
            {
                entry.SortedSetValue!.Remove(new SortedSetEntry(memberBytes, existingScore));
                newScore = existingScore + increment;
            }

            entry.SortedSetScores[memberBytes] = newScore;
            entry.SortedSetValue!.Add(new SortedSetEntry(memberBytes, newScore));
            return ValueTask.FromResult(newScore);
        }
    }

    public ValueTask<(byte[] Member, double Score)[]> ZRangeWithScoresAsync(string key, long start, long stop, bool descending, CancellationToken ct)
    {
        if (!_store.TryGetValue(key, out var entry))
            return ValueTask.FromResult(Array.Empty<(byte[] Member, double Score)>());

        lock (entry.Sync)
        {
            if (IsExpired(entry))
            {
                _store.TryRemove(key, out _);
                return ValueTask.FromResult(Array.Empty<(byte[] Member, double Score)>());
            }

            if (entry.Type != EntryType.SortedSet)
                return ValueTask.FromResult(Array.Empty<(byte[] Member, double Score)>());

            var count = entry.SortedSetValue!.Count;
            if (count == 0)
                return ValueTask.FromResult(Array.Empty<(byte[] Member, double Score)>());

            if (start < 0) start = count + start;
            if (stop < 0) stop = count + stop;

            start = Math.Max(0, Math.Min(start, count - 1));
            stop = Math.Max(0, Math.Min(stop, count - 1));

            if (start > stop)
                return ValueTask.FromResult(Array.Empty<(byte[] Member, double Score)>());

            var result = new List<(byte[] Member, double Score)>((int)(stop - start + 1));
            long idx = 0;
            IEnumerable<SortedSetEntry> source = descending ? entry.SortedSetValue.Reverse() : entry.SortedSetValue;
            foreach (var item in source)
            {
                if (idx >= start && idx <= stop)
                    result.Add((item.Member, item.Score));
                if (idx > stop)
                    break;
                idx++;
            }

            return ValueTask.FromResult(result.ToArray());
        }
    }

    public ValueTask<(byte[] Member, double Score)[]> ZRangeByScoreWithScoresAsync(
        string key,
        double min,
        double max,
        bool descending,
        long? offset,
        long? count,
        CancellationToken ct)
    {
        if (!_store.TryGetValue(key, out var entry))
            return ValueTask.FromResult(Array.Empty<(byte[] Member, double Score)>());

        lock (entry.Sync)
        {
            if (IsExpired(entry))
            {
                _store.TryRemove(key, out _);
                return ValueTask.FromResult(Array.Empty<(byte[] Member, double Score)>());
            }

            if (entry.Type != EntryType.SortedSet)
                return ValueTask.FromResult(Array.Empty<(byte[] Member, double Score)>());

            var result = new List<(byte[] Member, double Score)>();
            var skipped = 0L;
            var taken = 0L;
            IEnumerable<SortedSetEntry> source = descending ? entry.SortedSetValue!.Reverse() : entry.SortedSetValue!;
            foreach (var item in source)
            {
                if (item.Score < min || item.Score > max)
                    continue;

                if (offset.HasValue && skipped < offset.Value)
                {
                    skipped++;
                    continue;
                }

                result.Add((item.Member, item.Score));
                taken++;

                if (count.HasValue && taken >= count.Value)
                    break;
            }

            return ValueTask.FromResult(result.ToArray());
        }
    }

    // ========== RediSearch / RedisBloom / RedisTimeSeries ==========

    public ValueTask<bool> FtCreateAsync(string index, string prefix, string[] fields, CancellationToken ct)
        => ValueTask.FromResult(true);

    public ValueTask<string[]> FtSearchAsync(string index, string query, int? offset, int? count, CancellationToken ct)
        => ValueTask.FromResult(Array.Empty<string>());

    public ValueTask<bool> BfAddAsync(string key, ReadOnlyMemory<byte> item, CancellationToken ct)
    {
        var entry = _store.GetOrAdd(key, _ => new CacheEntry
        {
            Type = EntryType.Bloom,
            BloomValue = new ConcurrentDictionary<byte[], byte>(ByteArrayComparer.Instance)
        });

        lock (entry.Sync)
        {
            if (IsExpired(entry))
            {
                _store.TryRemove(key, out _);
                return ValueTask.FromResult(false);
            }

            if (entry.Type != EntryType.Bloom)
                throw new InvalidOperationException($"Key '{key}' is not a bloom filter");

            var added = entry.BloomValue!.TryAdd(item.ToArray(), 0);
            return ValueTask.FromResult(added);
        }
    }

    public ValueTask<bool> BfExistsAsync(string key, ReadOnlyMemory<byte> item, CancellationToken ct)
    {
        if (!_store.TryGetValue(key, out var entry))
            return ValueTask.FromResult(false);

        lock (entry.Sync)
        {
            if (IsExpired(entry))
            {
                _store.TryRemove(key, out _);
                return ValueTask.FromResult(false);
            }

            if (entry.Type != EntryType.Bloom)
                return ValueTask.FromResult(false);

            return ValueTask.FromResult(entry.BloomValue!.ContainsKey(item.ToArray()));
        }
    }

    public ValueTask<bool> TsCreateAsync(string key, CancellationToken ct)
    {
        var entry = _store.GetOrAdd(key, _ => new CacheEntry
        {
            Type = EntryType.TimeSeries,
            TimeSeriesValue = new SortedDictionary<long, double>()
        });

        lock (entry.Sync)
        {
            if (IsExpired(entry))
            {
                _store.TryRemove(key, out _);
                return ValueTask.FromResult(false);
            }

            if (entry.Type != EntryType.TimeSeries)
                throw new InvalidOperationException($"Key '{key}' is not a time series");

            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<long> TsAddAsync(string key, long timestamp, double value, CancellationToken ct)
    {
        var entry = _store.GetOrAdd(key, _ => new CacheEntry
        {
            Type = EntryType.TimeSeries,
            TimeSeriesValue = new SortedDictionary<long, double>()
        });

        lock (entry.Sync)
        {
            if (IsExpired(entry))
            {
                _store.TryRemove(key, out _);
                return ValueTask.FromResult(timestamp);
            }

            if (entry.Type != EntryType.TimeSeries)
                throw new InvalidOperationException($"Key '{key}' is not a time series");

            entry.TimeSeriesValue![timestamp] = value;
            return ValueTask.FromResult(timestamp);
        }
    }

    public ValueTask<(long Timestamp, double Value)[]> TsRangeAsync(string key, long from, long to, CancellationToken ct)
    {
        if (!_store.TryGetValue(key, out var entry))
            return ValueTask.FromResult(Array.Empty<(long Timestamp, double Value)>());

        lock (entry.Sync)
        {
            if (IsExpired(entry))
            {
                _store.TryRemove(key, out _);
                return ValueTask.FromResult(Array.Empty<(long Timestamp, double Value)>());
            }

            if (entry.Type != EntryType.TimeSeries)
                return ValueTask.FromResult(Array.Empty<(long Timestamp, double Value)>());

            var results = new List<(long Timestamp, double Value)>();
            foreach (var pair in entry.TimeSeriesValue!)
            {
                if (pair.Key < from || pair.Key > to)
                    continue;
                results.Add((pair.Key, pair.Value));
            }

            return ValueTask.FromResult(results.ToArray());
        }
    }

    // ========== Scan Commands ==========

    public async IAsyncEnumerable<string> ScanAsync(
        string? pattern = null,
        int pageSize = 128,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (pageSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageSize));

        foreach (var entry in _store.ToArray())
        {
            ct.ThrowIfCancellationRequested();

            if (IsExpired(entry.Value))
            {
                _store.TryRemove(entry.Key, out _);
                continue;
            }

            if (MatchesPattern(pattern, entry.Key))
                yield return entry.Key;
        }
    }

    public async IAsyncEnumerable<byte[]> SScanAsync(
        string key,
        string? pattern = null,
        int pageSize = 128,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (pageSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageSize));

        if (!_store.TryGetValue(key, out var entry))
            yield break;

        byte[][] members;
        lock (entry.Sync)
        {
            if (IsExpired(entry) || entry.Type != EntryType.Set)
                yield break;

            members = entry.SetValue!.Keys.ToArray();
        }

        foreach (var member in members)
        {
            ct.ThrowIfCancellationRequested();
            if (MatchesPattern(pattern, Encoding.UTF8.GetString(member)))
                yield return member;
        }
    }

    public async IAsyncEnumerable<(string Field, byte[] Value)> HScanAsync(
        string key,
        string? pattern = null,
        int pageSize = 128,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (pageSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageSize));

        if (!_store.TryGetValue(key, out var entry))
            yield break;

        KeyValuePair<string, byte[]>[] fields;
        lock (entry.Sync)
        {
            if (IsExpired(entry) || entry.Type != EntryType.Hash)
                yield break;

            fields = entry.HashValue!.ToArray();
        }

        foreach (var pair in fields)
        {
            ct.ThrowIfCancellationRequested();
            if (MatchesPattern(pattern, pair.Key))
                yield return (pair.Key, pair.Value);
        }
    }

    public async IAsyncEnumerable<(byte[] Member, double Score)> ZScanAsync(
        string key,
        string? pattern = null,
        int pageSize = 128,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (pageSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageSize));

        if (!_store.TryGetValue(key, out var entry))
            yield break;

        SortedSetEntry[] items;
        lock (entry.Sync)
        {
            if (IsExpired(entry) || entry.Type != EntryType.SortedSet)
                yield break;

            items = entry.SortedSetValue!.ToArray();
        }

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (MatchesPattern(pattern, Encoding.UTF8.GetString(item.Member)))
                yield return (item.Member, item.Score);
        }
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
        public object Sync { get; } = new();
        public EntryType Type { get; set; }
        public byte[]? StringValue { get; set; }
        public ConcurrentDictionary<string, byte[]>? HashValue { get; set; }
        public LinkedList<byte[]>? ListValue { get; set; }
        public ConcurrentDictionary<byte[], byte>? SetValue { get; set; }
        public SortedSet<SortedSetEntry>? SortedSetValue { get; set; }
        public Dictionary<byte[], double>? SortedSetScores { get; set; }
        public ConcurrentDictionary<byte[], byte>? BloomValue { get; set; }
        public SortedDictionary<long, double>? TimeSeriesValue { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
    }

    private enum EntryType
    {
        String,
        Hash,
        List,
        Set,
        SortedSet,
        Bloom,
        TimeSeries
    }

    private sealed record SortedSetEntry(byte[] Member, double Score);

    private sealed class SortedSetEntryComparer : IComparer<SortedSetEntry>
    {
        public static readonly SortedSetEntryComparer Instance = new();

        public int Compare(SortedSetEntry? x, SortedSetEntry? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var scoreCompare = x.Score.CompareTo(y.Score);
            if (scoreCompare != 0) return scoreCompare;

            return CompareBytes(x.Member, y.Member);
        }

        private static int CompareBytes(byte[] left, byte[] right)
        {
            var min = Math.Min(left.Length, right.Length);
            for (var i = 0; i < min; i++)
            {
                var cmp = left[i].CompareTo(right[i]);
                if (cmp != 0) return cmp;
            }
            return left.Length.CompareTo(right.Length);
        }
    }

    private static bool MatchesPattern(string? pattern, string value)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern == "*")
            return true;

        var p = 0;
        var v = 0;
        var starIdx = -1;
        var match = 0;

        while (v < value.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == value[v]))
            {
                p++;
                v++;
                continue;
            }

            if (p < pattern.Length && pattern[p] == '*')
            {
                starIdx = p++;
                match = v;
                continue;
            }

            if (starIdx != -1)
            {
                p = starIdx + 1;
                v = ++match;
                continue;
            }

            return false;
        }

        while (p < pattern.Length && pattern[p] == '*')
            p++;

        return p == pattern.Length;
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
