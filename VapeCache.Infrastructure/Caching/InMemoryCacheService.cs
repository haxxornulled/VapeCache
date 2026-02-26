using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;

namespace VapeCache.Infrastructure.Caching;

internal sealed class InMemoryCacheService : ICacheFallbackService
{
    private readonly IMemoryCache _cache;
    private readonly ICurrentCacheService _current;
    private readonly CacheStats _stats;
    private readonly IOptionsMonitor<InMemorySpillOptions> _spillOptionsMonitor;
    private InMemorySpillOptions SpillOptions => _spillOptionsMonitor.CurrentValue;
    private readonly IInMemorySpillStore _spillStore;
    private readonly bool _spillStoreSupportsWrites;
    private readonly ICacheIntentRegistry _intentRegistry;
    private static readonly ICacheIntentRegistry NoopIntentRegistry = new NoopCacheIntentRegistry();

    public InMemoryCacheService(
        IMemoryCache cache,
        ICurrentCacheService current,
        CacheStatsRegistry statsRegistry,
        IOptionsMonitor<InMemorySpillOptions> spillOptions,
        IInMemorySpillStore spillStore,
        ICacheIntentRegistry? intentRegistry = null)
    {
        _cache = cache;
        _current = current;
        _stats = statsRegistry.GetOrCreate(CacheStatsNames.Memory);
        _spillOptionsMonitor = spillOptions;
        _spillStore = spillStore;
        _intentRegistry = intentRegistry ?? NoopIntentRegistry;
        // Avoid writing spill references when the no-op store is active.
        // This prevents large entries from becoming unreadable in free-tier/default wiring.
        _spillStoreSupportsWrites = spillStore is not NoopSpillStore;
    }

    public string Name => "memory";

    public async ValueTask<byte[]?> GetAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ct.ThrowIfCancellationRequested();
        _current.SetCurrent(Name);

        _stats.IncGet();
        var start = Stopwatch.GetTimestamp();
        try
        {
            if (_cache.TryGetValue(key, out object? cached))
            {
                if (cached is byte[] value)
                {
                    _stats.IncHit();
                    CacheTelemetry.GetCalls.Add(1, new TagList { { "backend", Name } });
                    CacheTelemetry.Hits.Add(1, new TagList { { "backend", Name } });
                    return value;
                }

                if (cached is SpillEntry spill)
                {
                    var bytes = await TryReadSpillAsync(key, spill, ct).ConfigureAwait(false);
                    if (bytes is not null)
                    {
                        _stats.IncHit();
                        CacheTelemetry.GetCalls.Add(1, new TagList { { "backend", Name } });
                        CacheTelemetry.Hits.Add(1, new TagList { { "backend", Name } });
                        return bytes;
                    }
                }
            }

            _stats.IncMiss();
            _intentRegistry.RecordRemove(key);
            CacheTelemetry.GetCalls.Add(1, new TagList { { "backend", Name } });
            CacheTelemetry.Misses.Add(1, new TagList { { "backend", Name } });
            return null;
        }
        finally
        {
            CacheTelemetry.OpMs.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds, new TagList { { "backend", Name }, { "op", "get" } });
        }
    }

    public async ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CacheEntryOptions options, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ct.ThrowIfCancellationRequested();
        _current.SetCurrent(Name);
        _stats.IncSet();
        CacheTelemetry.SetCalls.Add(1, new TagList { { "backend", Name } });
        CacheTelemetry.SetPayloadBytes.Record(value.Length, new TagList
        {
            { "backend", Name },
            { "bucket", CacheTelemetry.GetPayloadBucket(value.Length) }
        });
        if (value.Length > 65536)
            CacheTelemetry.LargeKeyWrites.Add(1, new TagList { { "backend", Name } });
        var start = Stopwatch.GetTimestamp();
        try
        {
            var spillOptions = SpillOptions;
            if (ShouldSpill(value.Length, spillOptions))
            {
                var inlinePrefixBytes = Math.Max(0, spillOptions.InlinePrefixBytes);
                if (inlinePrefixBytes < value.Length)
                {
                    var spillRef = Guid.NewGuid();
                    var inlinePrefix = inlinePrefixBytes == 0 ? null : value.Slice(0, inlinePrefixBytes).ToArray();
                    var tail = value.Slice(inlinePrefixBytes);

                    try
                    {
                        await _spillStore.WriteAsync(spillRef, tail, ct).ConfigureAwait(false);
                        StoreEntry(key, options, new SpillEntry(spillRef, inlinePrefix, inlinePrefixBytes, value.Length));
                        _intentRegistry.RecordSet(key, Name, options, value.Length);
                        return;
                    }
                    catch
                    {
                        // Fall through to store in-memory if spill fails.
                    }
                }
            }

            StoreEntry(key, options, value);
            _intentRegistry.RecordSet(key, Name, options, value.Length);
        }
        finally
        {
            CacheTelemetry.OpMs.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds, new TagList { { "backend", Name }, { "op", "set" } });
        }
    }

    public ValueTask<bool> RemoveAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ct.ThrowIfCancellationRequested();
        _current.SetCurrent(Name);
        _stats.IncRemove();
        CacheTelemetry.RemoveCalls.Add(1, new TagList { { "backend", Name } });
        var start = Stopwatch.GetTimestamp();
        _cache.Remove(key);
        _intentRegistry.RecordRemove(key);
        CacheTelemetry.OpMs.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds, new TagList { { "backend", Name }, { "op", "remove" } });
        return ValueTask.FromResult(true);
    }

    public async ValueTask<T?> GetAsync<T>(string key, SpanDeserializer<T> deserialize, CancellationToken ct)
    {
        var bytes = await GetAsync(key, ct).ConfigureAwait(false);
        if (bytes is null) return default;
        return deserialize(bytes);
    }

    public ValueTask SetAsync<T>(string key, T value, Action<IBufferWriter<byte>, T> serialize, CacheEntryOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _current.SetCurrent(Name);
        var buffer = new ArrayBufferWriter<byte>(256);
        serialize(buffer, value);
        return SetAsync(key, buffer.WrittenMemory, options, ct);
    }

    public async ValueTask<T> GetOrSetAsync<T>(
        string key,
        Func<CancellationToken, ValueTask<T>> factory,
        Action<IBufferWriter<byte>, T> serialize,
        SpanDeserializer<T> deserialize,
        CacheEntryOptions options,
        CancellationToken ct)
    {
        var bytes = await GetAsync(key, ct).ConfigureAwait(false);
        if (bytes is not null)
            return deserialize(bytes);

        var created = await factory(ct).ConfigureAwait(false);
        await SetAsync(key, created, serialize, options, ct).ConfigureAwait(false);
        return created;
    }

    private bool ShouldSpill(int length, InMemorySpillOptions spillOptions)
    {
        return _spillStoreSupportsWrites &&
               spillOptions.EnableSpillToDisk &&
               spillOptions.SpillThresholdBytes > 0 &&
               length > spillOptions.SpillThresholdBytes;
    }

    private void StoreEntry(string key, CacheEntryOptions options, ReadOnlyMemory<byte> value)
    {
        var entry = _cache.CreateEntry(key);
        try
        {
            if (options.Ttl is not null)
                entry.AbsoluteExpirationRelativeToNow = options.Ttl;

            entry.RegisterPostEvictionCallback(static (_, _, reason, _) =>
            {
                CacheTelemetry.Evictions.Add(1, new TagList { { "backend", "memory" }, { "reason", reason.ToString().ToLowerInvariant() } });
            });

            if (MemoryMarshal.TryGetArray(value, out ArraySegment<byte> segment) &&
                segment.Array is not null &&
                segment.Offset == 0 &&
                segment.Count == segment.Array.Length)
            {
                entry.Value = segment.Array;
                return;
            }

            entry.Value = value.ToArray();
        }
        finally
        {
            entry.Dispose();
        }
    }

    private void StoreEntry(string key, CacheEntryOptions options, SpillEntry entryValue)
    {
        var entry = _cache.CreateEntry(key);
        try
        {
            if (options.Ttl is not null)
                entry.AbsoluteExpirationRelativeToNow = options.Ttl;

            entry.Value = entryValue;
            entry.RegisterPostEvictionCallback(static (_, value, reason, state) =>
            {
                CacheTelemetry.Evictions.Add(1, new TagList { { "backend", "memory" }, { "reason", reason.ToString().ToLowerInvariant() } });
                if (value is SpillEntry spill)
                {
                    var store = (IInMemorySpillStore)state!;
                    _ = store.DeleteAsync(spill.SpillRef, CancellationToken.None);
                }
            }, _spillStore);
        }
        finally
        {
            entry.Dispose();
        }
    }

    private async ValueTask<byte[]?> TryReadSpillAsync(string key, SpillEntry spill, CancellationToken ct)
    {
        try
        {
            var tail = await _spillStore.TryReadAsync(spill.SpillRef, ct).ConfigureAwait(false);
            if (tail is null)
            {
                _cache.Remove(key);
                return null;
            }

            var inlineLength = spill.InlineLength;
            if (inlineLength == 0)
                return tail;

            var expectedTail = spill.TotalLength - inlineLength;
            if (expectedTail != tail.Length)
            {
                _cache.Remove(key);
                return null;
            }

            var combined = new byte[spill.TotalLength];
            Buffer.BlockCopy(spill.InlinePrefix!, 0, combined, 0, inlineLength);
            Buffer.BlockCopy(tail, 0, combined, inlineLength, tail.Length);
            return combined;
        }
        catch
        {
            _cache.Remove(key);
            return null;
        }
    }

    private sealed class SpillEntry
    {
        public SpillEntry(Guid spillRef, byte[]? inlinePrefix, int inlineLength, int totalLength)
        {
            SpillRef = spillRef;
            InlinePrefix = inlinePrefix;
            InlineLength = inlineLength;
            TotalLength = totalLength;
        }

        public Guid SpillRef { get; }
        public byte[]? InlinePrefix { get; }
        public int InlineLength { get; }
        public int TotalLength { get; }
    }

    private sealed class NoopCacheIntentRegistry : ICacheIntentRegistry
    {
        public IReadOnlyList<CacheIntentEntry> GetRecent(int maxCount) => Array.Empty<CacheIntentEntry>();
        public void RecordRemove(string key) { }
        public void RecordSet(string key, string backend, in CacheEntryOptions options, int payloadBytes) { }
        public bool TryGet(string key, out CacheIntentEntry? entry)
        {
            entry = null;
            return false;
        }
    }
}
