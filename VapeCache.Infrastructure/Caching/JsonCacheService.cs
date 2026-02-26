using System.Text.Json;
using Microsoft.Extensions.Logging;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Abstractions.Modules;

namespace VapeCache.Infrastructure.Caching;

internal sealed class JsonCacheService : IJsonCache
{
    private static readonly JsonSerializerOptions DefaultOptions = new(JsonSerializerDefaults.Web);

    private readonly IRedisCommandExecutor _redis;
    private readonly ICacheService _cache;
    private readonly IRedisModuleDetector _modules;
    private readonly ILogger<JsonCacheService> _logger;
    private readonly JsonSerializerOptions _options;
    private readonly SemaphoreSlim _moduleGate = new(1, 1);
    private bool? _redisJsonAvailable;

    public JsonCacheService(
        IRedisCommandExecutor redis,
        ICacheService cache,
        IRedisModuleDetector modules,
        ILogger<JsonCacheService> logger,
        JsonSerializerOptions? options = null)
    {
        _redis = redis;
        _cache = cache;
        _modules = modules;
        _logger = logger;
        _options = options ?? DefaultOptions;
    }

    public async ValueTask<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_redisJsonAvailable.HasValue)
            return _redisJsonAvailable.Value;

        await _moduleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_redisJsonAvailable.HasValue)
                return _redisJsonAvailable.Value;

            var available = await _modules.HasRedisJsonAsync(ct).ConfigureAwait(false);
            _redisJsonAvailable = available;
            return available;
        }
        finally
        {
            _moduleGate.Release();
        }
    }

    public async ValueTask<T?> GetAsync<T>(string key, string? path = null, CancellationToken ct = default)
    {
        if (await IsAvailableAsync(ct).ConfigureAwait(false))
        {
            using var lease = await _redis.JsonGetLeaseAsync(key, path, ct).ConfigureAwait(false);
            if (lease.IsNull)
                return default;
            return JsonSerializer.Deserialize<T>(lease.Span, _options);
        }

        if (!IsRootPath(path))
            _logger.LogWarning("RedisJSON unavailable; JSON path '{Path}' ignored for key {Key}.", path, key);

        var bytes = await _cache.GetAsync(key, ct).ConfigureAwait(false);
        if (bytes is null)
            return default;
        return JsonSerializer.Deserialize<T>(bytes, _options);
    }

    public async ValueTask<RedisValueLease> GetLeaseAsync(string key, string? path = null, CancellationToken ct = default)
    {
        if (await IsAvailableAsync(ct).ConfigureAwait(false))
            return await _redis.JsonGetLeaseAsync(key, path, ct).ConfigureAwait(false);

        if (!IsRootPath(path))
            _logger.LogWarning("RedisJSON unavailable; JSON path '{Path}' ignored for key {Key}.", path, key);

        var bytes = await _cache.GetAsync(key, ct).ConfigureAwait(false);
        if (bytes is null)
            return RedisValueLease.Null;

        return new RedisValueLease(bytes, bytes.Length, pooled: false);
    }

    public async ValueTask SetAsync<T>(string key, T value, string? path = null, CancellationToken ct = default)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, _options);
        if (await IsAvailableAsync(ct).ConfigureAwait(false))
        {
            await _redis.JsonSetAsync(key, path ?? ".", json, ct).ConfigureAwait(false);
            return;
        }

        if (!IsRootPath(path))
            _logger.LogWarning("RedisJSON unavailable; JSON path '{Path}' ignored for key {Key}.", path, key);

        await _cache.SetAsync(key, json, default, ct).ConfigureAwait(false);
    }

    public async ValueTask SetLeaseAsync(string key, RedisValueLease json, string? path = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(json);

        if (await IsAvailableAsync(ct).ConfigureAwait(false))
        {
            await _redis.JsonSetLeaseAsync(key, path ?? ".", json, ct).ConfigureAwait(false);
            return;
        }

        if (!IsRootPath(path))
            _logger.LogWarning("RedisJSON unavailable; JSON path '{Path}' ignored for key {Key}.", path, key);

        await _cache.SetAsync(key, json.Memory, default, ct).ConfigureAwait(false);
    }

    public async ValueTask<long> DeleteAsync(string key, string? path = null, CancellationToken ct = default)
    {
        if (await IsAvailableAsync(ct).ConfigureAwait(false))
            return await _redis.JsonDelAsync(key, path, ct).ConfigureAwait(false);

        if (!IsRootPath(path))
            _logger.LogWarning("RedisJSON unavailable; JSON path '{Path}' ignored for key {Key}.", path, key);

        var removed = await _cache.RemoveAsync(key, ct).ConfigureAwait(false);
        return removed ? 1L : 0L;
    }

    private static bool IsRootPath(string? path)
        => string.IsNullOrWhiteSpace(path) || path == ".";
}
