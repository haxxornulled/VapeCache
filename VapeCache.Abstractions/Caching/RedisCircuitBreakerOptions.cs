namespace VapeCache.Abstractions.Caching;

public sealed record RedisCircuitBreakerOptions
{
    public bool Enabled { get; init; } = true;
    public int ConsecutiveFailuresToOpen { get; init; } = 3;
    public TimeSpan BreakDuration { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan HalfOpenProbeTimeout { get; init; } = TimeSpan.FromMilliseconds(250);
}
