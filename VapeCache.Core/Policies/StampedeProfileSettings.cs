namespace VapeCache.Core.Policies;

/// <summary>
/// Immutable settings payload used by stampede profile presets.
/// </summary>
/// <param name="Enabled">Whether stampede protection is enabled.</param>
/// <param name="MaxKeys">Maximum tracked lock keys.</param>
/// <param name="RejectSuspiciousKeys">Whether to reject suspicious keys.</param>
/// <param name="MaxKeyLength">Maximum allowed key length.</param>
/// <param name="LockWaitTimeout">Maximum wait time for a stampede lock.</param>
/// <param name="EnableFailureBackoff">Whether backoff is enabled after factory failure.</param>
/// <param name="FailureBackoff">Backoff duration after failed cache factory execution.</param>
public readonly record struct StampedeProfileSettings(
    bool Enabled,
    int MaxKeys,
    bool RejectSuspiciousKeys,
    int MaxKeyLength,
    TimeSpan LockWaitTimeout,
    bool EnableFailureBackoff,
    TimeSpan FailureBackoff);
