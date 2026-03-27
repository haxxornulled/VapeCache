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
public readonly record struct StampedeProfileSettings
{
    public StampedeProfileSettings(
        bool Enabled,
        int MaxKeys,
        bool RejectSuspiciousKeys,
        int MaxKeyLength,
        TimeSpan LockWaitTimeout,
        bool EnableFailureBackoff,
        TimeSpan FailureBackoff)
    {
        this.Enabled = Enabled;
        this.MaxKeys = MaxKeys;
        this.RejectSuspiciousKeys = RejectSuspiciousKeys;
        this.MaxKeyLength = MaxKeyLength;
        this.LockWaitTimeout = LockWaitTimeout;
        this.EnableFailureBackoff = EnableFailureBackoff;
        this.FailureBackoff = FailureBackoff;
    }

    public bool Enabled { get; init; }
    public int MaxKeys { get; init; }
    public bool RejectSuspiciousKeys { get; init; }
    public int MaxKeyLength { get; init; }
    public TimeSpan LockWaitTimeout { get; init; }
    public bool EnableFailureBackoff { get; init; }
    public TimeSpan FailureBackoff { get; init; }
}
