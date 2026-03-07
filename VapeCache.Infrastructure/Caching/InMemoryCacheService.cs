using System.Buffers;
using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Diagnostics;

namespace VapeCache.Infrastructure.Caching;

internal sealed partial class InMemoryCacheService : ICacheFallbackService
{
    private readonly IMemoryCache _cache;
    private readonly ICurrentCacheService _current;
    private readonly CacheStats _stats;
    private readonly IOptionsMonitor<InMemorySpillOptions> _spillOptionsMonitor;
    private InMemorySpillOptions SpillOptions => _spillOptionsMonitor.CurrentValue;
    private readonly IInMemorySpillStore _spillStore;
    private readonly bool _spillStoreSupportsWrites;
    private readonly ICacheIntentRegistry _intentRegistry;
    private readonly ILogger<InMemoryCacheService> _logger;
    private int _spillStoreWarningIssued;
    private static readonly ICacheIntentRegistry NoopIntentRegistry = new NoopCacheIntentRegistry();

    public InMemoryCacheService(
        IMemoryCache cache,
        ICurrentCacheService current,
        CacheStatsRegistry statsRegistry,
        IOptionsMonitor<InMemorySpillOptions> spillOptions,
        IInMemorySpillStore spillStore,
        ICacheIntentRegistry? intentRegistry = null,
        ILogger<InMemoryCacheService>? logger = null)
    {
        _cache = cache;
        _current = current;
        _stats = statsRegistry.GetOrCreate(CacheStatsNames.Memory);
        _spillOptionsMonitor = spillOptions;
        _spillStore = spillStore;
        _intentRegistry = intentRegistry ?? NoopIntentRegistry;
        _logger = logger ?? NullLogger<InMemoryCacheService>.Instance;
        // Avoid writing spill references when the no-op store is active.
        // This prevents large entries from becoming unreadable in free-tier/default wiring.
        _spillStoreSupportsWrites = spillStore is not NoopSpillStore;
        if (spillStore is ISpillStoreDiagnostics spillDiagnostics)
            CacheTelemetry.InitializeSpillDiagnostics(spillDiagnostics);
    }

    public string Name => "memory";

    /// <summary>
    /// Gets value.
    /// </summary>
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
                    return CopyBuffer(value);
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

    /// <summary>
    /// Sets value.
    /// </summary>
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
            await TryDeleteExistingSpillAsync(key, ct).ConfigureAwait(false);

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
                        _intentRegistry.RecordSet(key, BackendType.InMemory, options, value.Length);
                        return;
                    }
                    catch
                    {
                        // Fall through to store in-memory if spill fails.
                    }
                }
            }

            StoreEntry(key, options, value);
            _intentRegistry.RecordSet(key, BackendType.InMemory, options, value.Length);
        }
        finally
        {
            CacheTelemetry.OpMs.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds, new TagList { { "backend", Name }, { "op", "set" } });
        }
    }

    /// <summary>
    /// Removes value.
    /// </summary>
    public async ValueTask<bool> RemoveAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ct.ThrowIfCancellationRequested();
        _current.SetCurrent(Name);
        _stats.IncRemove();
        CacheTelemetry.RemoveCalls.Add(1, new TagList { { "backend", Name } });
        var start = Stopwatch.GetTimestamp();
        var existed = false;
        if (_cache.TryGetValue(key, out object? current))
        {
            existed = true;
            if (current is SpillEntry spill)
                await TryDeleteSpillRefAsync(spill.SpillRef, ct).ConfigureAwait(false);
        }

        _cache.Remove(key);
        _intentRegistry.RecordRemove(key);
        CacheTelemetry.OpMs.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds, new TagList { { "backend", Name }, { "op", "remove" } });
        return existed;
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
        if (spillOptions.EnableSpillToDisk && !_spillStoreSupportsWrites)
            ReportSpillStoreUnavailable();

        return _spillStoreSupportsWrites &&
               spillOptions.EnableSpillToDisk &&
               spillOptions.SpillThresholdBytes > 0 &&
               length > spillOptions.SpillThresholdBytes;
    }

    private void ReportSpillStoreUnavailable()
    {
        if (Interlocked.Exchange(ref _spillStoreWarningIssued, 1) != 0)
            return;

        CacheTelemetry.SpillStoreUnavailable.Add(1);
        LogSpillStoreUnavailable(_logger);
    }

    [LoggerMessage(
        EventId = 7101,
        Level = LogLevel.Warning,
        Message = "InMemory spill-to-disk is enabled, but the active spill store is no-op. Register persistence spill services (AddVapeCachePersistence) to enable file-backed scatter spill.")]
    private static partial void LogSpillStoreUnavailable(ILogger logger);

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

            entry.Value = CopyBuffer(value.Span);
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
                    _ = DeleteSpillRefBestEffortAsync(store, spill.SpillRef);
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
                await TryDeleteSpillRefAsync(spill.SpillRef, CancellationToken.None).ConfigureAwait(false);
                return null;
            }

            var inlineLength = spill.InlineLength;
            if (inlineLength == 0)
                return tail;

            var expectedTail = spill.TotalLength - inlineLength;
            if (expectedTail != tail.Length)
            {
                _cache.Remove(key);
                await TryDeleteSpillRefAsync(spill.SpillRef, CancellationToken.None).ConfigureAwait(false);
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
            await TryDeleteSpillRefAsync(spill.SpillRef, CancellationToken.None).ConfigureAwait(false);
            return null;
        }
    }

    private async ValueTask TryDeleteExistingSpillAsync(string key, CancellationToken ct)
    {
        if (_cache.TryGetValue(key, out object? existing) && existing is SpillEntry spill)
            await TryDeleteSpillRefAsync(spill.SpillRef, ct).ConfigureAwait(false);
    }

    private async ValueTask TryDeleteSpillRefAsync(Guid spillRef, CancellationToken ct)
    {
        try
        {
            await _spillStore.DeleteAsync(spillRef, ct).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort cleanup only; never fail cache operations on spill delete.
        }
    }

    private static async Task DeleteSpillRefBestEffortAsync(IInMemorySpillStore store, Guid spillRef)
    {
        try
        {
            await store.DeleteAsync(spillRef, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort cleanup only; this runs from an eviction callback.
        }
    }

    private static byte[] CopyBuffer(ReadOnlySpan<byte> source)
    {
        if (source.IsEmpty)
            return Array.Empty<byte>();

        var buffer = GC.AllocateUninitializedArray<byte>(source.Length);
        source.CopyTo(buffer);
        return buffer;
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
        /// <summary>
        /// Gets value.
        /// </summary>
        public IReadOnlyList<CacheIntentEntry> GetRecent(int maxCount) => Array.Empty<CacheIntentEntry>();
        /// <summary>
        /// Executes value.
        /// </summary>
        public void RecordRemove(string key) { }
        /// <summary>
        /// Executes value.
        /// </summary>
        public void RecordSet(string key, BackendType backend, in CacheEntryOptions options, int payloadBytes) { }
        /// <summary>
        /// Attempts to value.
        /// </summary>
        public bool TryGet(string key, out CacheIntentEntry? entry)
        {
            entry = null;
            return false;
        }
    }
}
