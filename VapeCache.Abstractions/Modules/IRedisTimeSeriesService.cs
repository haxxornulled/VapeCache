namespace VapeCache.Abstractions.Modules;

/// <summary>
/// RedisTimeSeries integration for time-series data.
/// </summary>
public interface IRedisTimeSeriesService
{
    ValueTask<bool> IsAvailableAsync(CancellationToken ct = default);
    ValueTask<bool> CreateSeriesAsync(string key, CancellationToken ct = default);
    ValueTask<long> AddAsync(string key, long timestamp, double value, CancellationToken ct = default);
    ValueTask<(long Timestamp, double Value)[]> RangeAsync(string key, long from, long to, CancellationToken ct = default);
}
