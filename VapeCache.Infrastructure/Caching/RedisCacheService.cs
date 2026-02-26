using System.Buffers;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Infrastructure.Caching;

internal sealed class RedisCacheService : ICacheService
{
    private readonly IRedisCommandExecutor _redis;
    private readonly ICurrentCacheService _current;
    private readonly CacheStats _stats;
    private readonly ICacheIntentRegistry _intentRegistry;
    private static readonly ICacheIntentRegistry NoopIntentRegistry = new NoopCacheIntentRegistry();

    [ActivatorUtilitiesConstructor]
    public RedisCacheService(RedisCommandExecutor redis, ICurrentCacheService current, CacheStatsRegistry statsRegistry, ICacheIntentRegistry? intentRegistry = null)
        : this((IRedisCommandExecutor)redis, current, statsRegistry, intentRegistry)
    {
    }

    public RedisCacheService(IRedisCommandExecutor redis, ICurrentCacheService current, CacheStatsRegistry statsRegistry, ICacheIntentRegistry? intentRegistry = null)
    {
        _redis = redis;
        _current = current;
        _stats = statsRegistry.GetOrCreate(CacheStatsNames.Redis);
        _intentRegistry = intentRegistry ?? NoopIntentRegistry;
    }

    public string Name => "redis";

    /// <summary>
    /// Gets value.
    /// </summary>
    public async ValueTask<byte[]?> GetAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _current.SetCurrent(Name);
        _stats.IncGet();
        CacheTelemetry.GetCalls.Add(1, new TagList { { "backend", Name } });
        var start = Stopwatch.GetTimestamp();
        try
        {
            var bytes = await _redis.GetAsync(key, ct).ConfigureAwait(false);
            if (bytes is null)
            {
                _intentRegistry.RecordRemove(key);
                _stats.IncMiss();
                CacheTelemetry.Misses.Add(1, new TagList { { "backend", Name } });
            }
            else
            {
                _stats.IncHit();
                CacheTelemetry.Hits.Add(1, new TagList { { "backend", Name } });
            }
            return bytes;
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
        var ok = await _redis.SetAsync(key, value, options.Ttl, ct).ConfigureAwait(false);
        CacheTelemetry.OpMs.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds, new TagList { { "backend", Name }, { "op", "set" } });
        if (!ok) throw new InvalidOperationException("Redis SET failed.");
        _intentRegistry.RecordSet(key, Name, options, value.Length);
    }

    /// <summary>
    /// Removes value.
    /// </summary>
    public async ValueTask<bool> RemoveAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _current.SetCurrent(Name);
        _stats.IncRemove();
        CacheTelemetry.RemoveCalls.Add(1, new TagList { { "backend", Name } });
        var start = Stopwatch.GetTimestamp();
        try
        {
            var removed = await _redis.DeleteAsync(key, ct).ConfigureAwait(false);
            if (removed)
                _intentRegistry.RecordRemove(key);
            return removed;
        }
        finally
        {
            CacheTelemetry.OpMs.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds, new TagList { { "backend", Name }, { "op", "remove" } });
        }
    }

    public async ValueTask<T?> GetAsync<T>(string key, SpanDeserializer<T> deserialize, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _current.SetCurrent(Name);
        _stats.IncGet();
        CacheTelemetry.GetCalls.Add(1, new TagList { { "backend", Name } });
        var start = Stopwatch.GetTimestamp();
        try
        {
            using var lease = await _redis.GetLeaseAsync(key, ct).ConfigureAwait(false);
            if (lease.IsNull)
            {
                _intentRegistry.RecordRemove(key);
                _stats.IncMiss();
                CacheTelemetry.Misses.Add(1, new TagList { { "backend", Name } });
                return default;
            }

            _stats.IncHit();
            CacheTelemetry.Hits.Add(1, new TagList { { "backend", Name } });
            return deserialize(lease.Span);
        }
        finally
        {
            CacheTelemetry.OpMs.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds, new TagList { { "backend", Name }, { "op", "get" } });
        }
    }

    public async ValueTask SetAsync<T>(string key, T value, Action<IBufferWriter<byte>, T> serialize, CacheEntryOptions options, CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        serialize(buffer, value);
        await SetAsync(key, buffer.WrittenMemory, options, ct).ConfigureAwait(false);
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
        public void RecordSet(string key, string backend, in CacheEntryOptions options, int payloadBytes) { }
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
