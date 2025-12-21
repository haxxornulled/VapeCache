namespace VapeCache.Application.Caching;

public interface IRedisCircuitBreakerState
{
    bool Enabled { get; }
    bool IsOpen { get; }
    int ConsecutiveFailures { get; }
    TimeSpan? OpenRemaining { get; }
    bool HalfOpenProbeInFlight { get; }
}

