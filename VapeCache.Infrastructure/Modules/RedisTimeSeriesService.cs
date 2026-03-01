using Microsoft.Extensions.Logging;
using VapeCache.Abstractions.Connections;
using VapeCache.Abstractions.Modules;

namespace VapeCache.Infrastructure.Modules;

internal sealed class RedisTimeSeriesService : IRedisTimeSeriesService
{
    private readonly IRedisCommandExecutor _redis;
    private readonly IRedisFallbackCommandExecutor _fallback;
    private readonly IRedisModuleDetector _modules;
    private readonly ILogger<RedisTimeSeriesService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool? _available;

    public RedisTimeSeriesService(
        IRedisCommandExecutor redis,
        IRedisFallbackCommandExecutor fallback,
        IRedisModuleDetector modules,
        ILogger<RedisTimeSeriesService> logger)
    {
        _redis = redis;
        _fallback = fallback;
        _modules = modules;
        _logger = logger;
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_available == true)
            return true;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_available == true)
                return true;

            var available = await _modules.IsModuleInstalledAsync("timeseries", ct).ConfigureAwait(false);
            if (available)
                _available = true;
            return available;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Creates value.
    /// </summary>
    public async ValueTask<bool> CreateSeriesAsync(string key, CancellationToken ct = default)
    {
        if (await IsAvailableAsync(ct).ConfigureAwait(false))
            return await _redis.TsCreateAsync(key, ct).ConfigureAwait(false);

        _logger.LogDebug("RedisTimeSeries unavailable; using in-memory fallback for {Key}.", key);
        return await _fallback.TsCreateAsync(key, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds value.
    /// </summary>
    public async ValueTask<long> AddAsync(string key, long timestamp, double value, CancellationToken ct = default)
    {
        if (await IsAvailableAsync(ct).ConfigureAwait(false))
            return await _redis.TsAddAsync(key, timestamp, value, ct).ConfigureAwait(false);

        _logger.LogDebug("RedisTimeSeries unavailable; using in-memory fallback for {Key}.", key);
        return await _fallback.TsAddAsync(key, timestamp, value, ct).ConfigureAwait(false);
    }

    public async ValueTask<(long Timestamp, double Value)[]> RangeAsync(string key, long from, long to, CancellationToken ct = default)
    {
        if (await IsAvailableAsync(ct).ConfigureAwait(false))
            return await _redis.TsRangeAsync(key, from, to, ct).ConfigureAwait(false);

        _logger.LogDebug("RedisTimeSeries unavailable; using in-memory fallback for {Key}.", key);
        return await _fallback.TsRangeAsync(key, from, to, ct).ConfigureAwait(false);
    }
}
