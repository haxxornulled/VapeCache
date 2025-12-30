using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Caching.Memory;
using VapeCache.Abstractions.Caching;

namespace VapeCache.Infrastructure.Caching;

internal sealed class InMemoryCacheService : ICacheService
{
    private readonly IMemoryCache _cache;
    private readonly ICurrentCacheService _current;
    private readonly CacheStats _stats;

    public InMemoryCacheService(IMemoryCache cache, ICurrentCacheService current, CacheStatsRegistry statsRegistry)
    {
        _cache = cache;
        _current = current;
        _stats = statsRegistry.GetOrCreate(CacheStatsNames.Memory);
    }

    public string Name => "memory";

    public ValueTask<byte[]?> GetAsync(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _current.SetCurrent(Name);

        _stats.IncGet();
        var start = Stopwatch.GetTimestamp();
        try
        {
            if (_cache.TryGetValue(key, out byte[]? value))
            {
                _stats.IncHit();
                CacheTelemetry.GetCalls.Add(1, new TagList { { "backend", Name } });
                CacheTelemetry.Hits.Add(1, new TagList { { "backend", Name } });
                return ValueTask.FromResult<byte[]?>(value);
            }

            _stats.IncMiss();
            CacheTelemetry.GetCalls.Add(1, new TagList { { "backend", Name } });
            CacheTelemetry.Misses.Add(1, new TagList { { "backend", Name } });
            return ValueTask.FromResult<byte[]?>(null);
        }
        finally
        {
            CacheTelemetry.OpMs.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds, new TagList { { "backend", Name }, { "op", "get" } });
        }
    }

    public ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CacheEntryOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _current.SetCurrent(Name);
        _stats.IncSet();
        CacheTelemetry.SetCalls.Add(1, new TagList { { "backend", Name } });
        var start = Stopwatch.GetTimestamp();
        var entry = _cache.CreateEntry(key);
        try
        {
            if (options.Ttl is not null)
                entry.AbsoluteExpirationRelativeToNow = options.Ttl;
            if (MemoryMarshal.TryGetArray(value, out ArraySegment<byte> segment) &&
                segment.Array is not null &&
                segment.Offset == 0 &&
                segment.Count == segment.Array.Length)
            {
                entry.Value = segment.Array;
            }
            else
            {
                entry.Value = value.ToArray();
            }
        }
        finally
        {
            entry.Dispose();
            CacheTelemetry.OpMs.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds, new TagList { { "backend", Name }, { "op", "set" } });
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> RemoveAsync(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _current.SetCurrent(Name);
        _stats.IncRemove();
        CacheTelemetry.RemoveCalls.Add(1, new TagList { { "backend", Name } });
        var start = Stopwatch.GetTimestamp();
        _cache.Remove(key);
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
}
