using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using VapeCache.Abstractions.Connections;
using VapeCache.Abstractions.Modules;

namespace VapeCache.Infrastructure.Modules;

internal sealed class RedisTimeSeriesService : IRedisTimeSeriesService
{
    private readonly IRedisCommandExecutor _redis;
    private readonly IRedisModuleDetector _modules;
    private readonly ILogger<RedisTimeSeriesService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool? _available;

    private readonly ConcurrentDictionary<string, SortedDictionary<long, double>> _series = new();
    private readonly ConcurrentDictionary<string, System.Threading.Lock> _locks = new();

    public RedisTimeSeriesService(IRedisCommandExecutor redis, IRedisModuleDetector modules, ILogger<RedisTimeSeriesService> logger)
    {
        _redis = redis;
        _modules = modules;
        _logger = logger;
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_available.HasValue)
            return _available.Value;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_available.HasValue)
                return _available.Value;

            var available = await _modules.IsModuleInstalledAsync("timeseries", ct).ConfigureAwait(false);
            _available = available;
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
        _series.GetOrAdd(key, _ => new SortedDictionary<long, double>());
        _locks.GetOrAdd(key, _ => new System.Threading.Lock());
        return true;
    }

    /// <summary>
    /// Adds value.
    /// </summary>
    public async ValueTask<long> AddAsync(string key, long timestamp, double value, CancellationToken ct = default)
    {
        if (await IsAvailableAsync(ct).ConfigureAwait(false))
            return await _redis.TsAddAsync(key, timestamp, value, ct).ConfigureAwait(false);

        _logger.LogDebug("RedisTimeSeries unavailable; using in-memory fallback for {Key}.", key);
        var series = _series.GetOrAdd(key, _ => new SortedDictionary<long, double>());
        var gate = _locks.GetOrAdd(key, _ => new System.Threading.Lock());
        lock (gate)
        {
            series[timestamp] = value;
        }
        return timestamp;
    }

    public async ValueTask<(long Timestamp, double Value)[]> RangeAsync(string key, long from, long to, CancellationToken ct = default)
    {
        if (await IsAvailableAsync(ct).ConfigureAwait(false))
            return await _redis.TsRangeAsync(key, from, to, ct).ConfigureAwait(false);

        if (!_series.TryGetValue(key, out var series))
            return Array.Empty<(long Timestamp, double Value)>();

        var gate = _locks.GetOrAdd(key, _ => new System.Threading.Lock());
        var results = new List<(long Timestamp, double Value)>();
        lock (gate)
        {
            foreach (var pair in series)
            {
                if (pair.Key < from || pair.Key > to)
                    continue;
                results.Add((pair.Key, pair.Value));
            }
        }
        return results.ToArray();
    }
}
