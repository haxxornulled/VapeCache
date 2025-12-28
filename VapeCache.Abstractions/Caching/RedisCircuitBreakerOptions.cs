namespace VapeCache.Abstractions.Caching;

/// <summary>
/// Configuration options for the Redis circuit breaker pattern.
/// Validation is enforced at startup to ensure safe operation.
/// </summary>
public sealed record RedisCircuitBreakerOptions
{
    /// <summary>
    /// Whether the circuit breaker is enabled. When false, no fallback occurs.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Number of consecutive failures before opening the circuit. Must be at least 2 (Polly constraint).
    /// Set to 2 for near-immediate failover (recommended for developer experience).
    /// </summary>
    public int ConsecutiveFailuresToOpen { get; init; } = 2; // Polly requires minimum 2

    /// <summary>
    /// Duration to keep the circuit open before attempting a half-open probe. Must be greater than zero.
    /// This is the base duration - if UseExponentialBackoff is true, actual duration increases with retries.
    /// </summary>
    public TimeSpan BreakDuration { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Timeout for half-open probe attempts. Must be greater than zero to prevent indefinite hangs.
    /// </summary>
    public TimeSpan HalfOpenProbeTimeout { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Maximum number of consecutive retry attempts before giving up completely.
    /// Set to 0 for infinite retries (circuit will keep trying forever).
    /// Default: 0 (infinite retries - never give up on Redis recovery).
    /// </summary>
    public int MaxConsecutiveRetries { get; init; } = 0;

    /// <summary>
    /// Whether to use exponential backoff for retry delays.
    /// When true, BreakDuration doubles after each failed retry (up to MaxBreakDuration).
    /// When false, BreakDuration remains constant for all retries.
    /// </summary>
    public bool UseExponentialBackoff { get; init; } = true; // Default: enabled

    /// <summary>
    /// Maximum break duration when using exponential backoff.
    /// Prevents exponential backoff from growing indefinitely.
    /// </summary>
    public TimeSpan MaxBreakDuration { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// MED-3 FIX: Maximum concurrent half-open probes allowed during circuit recovery.
    /// Prevents thundering herd when circuit closes - limits simultaneous Redis attempts.
    /// Default: 5 (allows gradual recovery without overwhelming Redis)
    /// </summary>
    public int MaxHalfOpenProbes { get; init; } = 5;
}
