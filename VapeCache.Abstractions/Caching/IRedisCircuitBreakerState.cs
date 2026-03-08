namespace VapeCache.Abstractions.Caching;

/// <summary>
/// Defines the redis circuit breaker state contract.
/// </summary>
public interface IRedisCircuitBreakerState
{
    /// <summary>
    /// Gets the enabled.
    /// </summary>
    bool Enabled { get; }
    /// <summary>
    /// Gets the s open.
    /// </summary>
    bool IsOpen { get; }
    /// <summary>
    /// Gets the consecutive failures.
    /// </summary>
    int ConsecutiveFailures { get; }
    /// <summary>
    /// Gets the open remaining.
    /// </summary>
    TimeSpan? OpenRemaining { get; }
    /// <summary>
    /// Gets the half open probe in flight.
    /// </summary>
    bool HalfOpenProbeInFlight { get; }
}
