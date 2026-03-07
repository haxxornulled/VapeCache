namespace VapeCache.Extensions.Aspire;

internal sealed class RedisExporterMetricsState
{
    private RedisExporterMetricsSnapshot _current = RedisExporterMetricsSnapshot.Unknown;

    public RedisExporterMetricsSnapshot Current => Volatile.Read(ref _current);

    public void SetDisabled(DateTimeOffset observedAtUtc)
    {
        Volatile.Write(ref _current, RedisExporterMetricsSnapshot.Disabled(observedAtUtc));
    }

    public void SetFailure(DateTimeOffset observedAtUtc)
    {
        var previous = Volatile.Read(ref _current);
        Volatile.Write(ref _current, RedisExporterMetricsSnapshot.Failed(previous, observedAtUtc));
    }

    public void SetSuccess(in RedisExporterMetricValues values, DateTimeOffset observedAtUtc)
    {
        Volatile.Write(ref _current, RedisExporterMetricsSnapshot.Success(values, observedAtUtc));
    }
}
