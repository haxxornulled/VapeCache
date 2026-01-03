using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using VapeCache.Abstractions.Connections;
using VapeCache.Abstractions.Modules;

namespace VapeCache.Infrastructure.Modules;

internal sealed class RedisBloomService : IRedisBloomService
{
    private readonly IRedisCommandExecutor _redis;
    private readonly IRedisModuleDetector _modules;
    private readonly ILogger<RedisBloomService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool? _available;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<byte[], byte>> _fallback = new();

    public RedisBloomService(IRedisCommandExecutor redis, IRedisModuleDetector modules, ILogger<RedisBloomService> logger)
    {
        _redis = redis;
        _modules = modules;
        _logger = logger;
    }

    public async ValueTask<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_available.HasValue)
            return _available.Value;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_available.HasValue)
                return _available.Value;

            var available = await _modules.IsModuleInstalledAsync("bf", ct).ConfigureAwait(false);
            _available = available;
            return available;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<bool> AddAsync(string key, ReadOnlyMemory<byte> item, CancellationToken ct = default)
    {
        if (await IsAvailableAsync(ct).ConfigureAwait(false))
            return await _redis.BfAddAsync(key, item, ct).ConfigureAwait(false);

        _logger.LogDebug("RedisBloom unavailable; using in-memory fallback for {Key}.", key);
        var set = _fallback.GetOrAdd(key, _ => new ConcurrentDictionary<byte[], byte>(ByteArrayComparer.Instance));
        return set.TryAdd(item.ToArray(), 0);
    }

    public async ValueTask<bool> ExistsAsync(string key, ReadOnlyMemory<byte> item, CancellationToken ct = default)
    {
        if (await IsAvailableAsync(ct).ConfigureAwait(false))
            return await _redis.BfExistsAsync(key, item, ct).ConfigureAwait(false);

        var set = _fallback.GetOrAdd(key, _ => new ConcurrentDictionary<byte[], byte>(ByteArrayComparer.Instance));
        return set.ContainsKey(item.ToArray());
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
