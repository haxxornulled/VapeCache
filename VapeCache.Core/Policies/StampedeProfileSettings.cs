namespace VapeCache.Core.Policies;

public readonly record struct StampedeProfileSettings(
    bool Enabled,
    int MaxKeys,
    bool RejectSuspiciousKeys,
    int MaxKeyLength,
    TimeSpan LockWaitTimeout,
    bool EnableFailureBackoff,
    TimeSpan FailureBackoff);
