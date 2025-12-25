using System.Buffers;
using System.Diagnostics;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Infrastructure.Caching;

internal sealed class RedisCacheService(IRedisCommandExecutor redis, ICurrentCacheService current, CacheStats stats) : ICacheService
{
    public string Name => "redis";

    public async ValueTask<byte[]?> GetAsync(string key, CancellationToken ct)
    {
        current.SetCurrent(Name);
        stats.IncGet();
        CacheTelemetry.GetCalls.Add(1, new TagList { { "backend", Name } });
        var start = Stopwatch.GetTimestamp();
        try
        {
            var bytes = await redis.GetAsync(key, ct).ConfigureAwait(false);
            if (bytes is null)
            {
                stats.IncMiss();
                CacheTelemetry.Misses.Add(1, new TagList { { "backend", Name } });
            }
            else
            {
                stats.IncHit();
                CacheTelemetry.Hits.Add(1, new TagList { { "backend", Name } });
            }
            return bytes;
        }
        finally
        {
            CacheTelemetry.OpMs.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds, new TagList { { "backend", Name }, { "op", "get" } });
        }
    }

    public async ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CacheEntryOptions options, CancellationToken ct)
    {
        current.SetCurrent(Name);
        stats.IncSet();
        CacheTelemetry.SetCalls.Add(1, new TagList { { "backend", Name } });
        var start = Stopwatch.GetTimestamp();
        var ok = await redis.SetAsync(key, value, options.Ttl, ct).ConfigureAwait(false);
        CacheTelemetry.OpMs.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds, new TagList { { "backend", Name }, { "op", "set" } });
        if (!ok) throw new InvalidOperationException("Redis SET failed.");
    }

    public async ValueTask<bool> RemoveAsync(string key, CancellationToken ct)
    {
        current.SetCurrent(Name);
        stats.IncRemove();
        CacheTelemetry.RemoveCalls.Add(1, new TagList { { "backend", Name } });
        var start = Stopwatch.GetTimestamp();
        try
        {
            return await redis.DeleteAsync(key, ct).ConfigureAwait(false);
        }
        finally
        {
            CacheTelemetry.OpMs.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds, new TagList { { "backend", Name }, { "op", "remove" } });
        }
    }

    public async ValueTask<T?> GetAsync<T>(string key, SpanDeserializer<T> deserialize, CancellationToken ct)
    {
        var bytes = await GetAsync(key, ct).ConfigureAwait(false);
        if (bytes is null) return default;
        return deserialize(bytes);
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
}
