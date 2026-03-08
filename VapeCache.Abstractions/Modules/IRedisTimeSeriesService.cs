namespace VapeCache.Abstractions.Modules;

/// <summary>
/// RedisTimeSeries integration for time-series data.
/// </summary>
public interface IRedisTimeSeriesService
{
    /// <summary>
    /// Executes s available async.
    /// </summary>
    ValueTask<bool> IsAvailableAsync(CancellationToken ct = default);
    /// <summary>
    /// Executes create series async.
    /// </summary>
    ValueTask<bool> CreateSeriesAsync(string key, CancellationToken ct = default);
    /// <summary>
    /// Executes add async.
    /// </summary>
    ValueTask<long> AddAsync(string key, long timestamp, double value, CancellationToken ct = default);
    /// <summary>
    /// Executes range async.
    /// </summary>
    ValueTask<(long Timestamp, double Value)[]> RangeAsync(string key, long from, long to, CancellationToken ct = default);
}
