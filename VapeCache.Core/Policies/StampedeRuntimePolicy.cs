namespace VapeCache.Core.Policies;

/// <summary>
/// Domain policy decisions for runtime stampede protection behavior.
/// </summary>
public static class StampedeRuntimePolicy
{
    /// <summary>
    /// Validates a cache key against runtime stampede policy constraints.
    /// </summary>
    public static StampedeKeyValidationResult ValidateKey(string key, bool rejectSuspiciousKeys, int maxKeyLength)
    {
        if (!rejectSuspiciousKeys)
            return StampedeKeyValidationResult.Valid;

        if (string.IsNullOrWhiteSpace(key))
            return StampedeKeyValidationResult.Rejected(StampedeKeyRejectionReason.Empty);

        if (key.Length > maxKeyLength)
            return StampedeKeyValidationResult.Rejected(StampedeKeyRejectionReason.MaxLength);

        for (var i = 0; i < key.Length; i++)
        {
            if (char.IsControl(key[i]))
                return StampedeKeyValidationResult.Rejected(StampedeKeyRejectionReason.ControlCharacter);
        }

        return StampedeKeyValidationResult.Valid;
    }

    /// <summary>
    /// Returns whether failure-backoff is active for the current configuration.
    /// </summary>
    public static bool IsFailureBackoffConfigured(bool enabled, TimeSpan backoff) =>
        enabled && backoff > TimeSpan.Zero;

    /// <summary>
    /// Returns whether the current time is still inside the retry-backoff window.
    /// </summary>
    public static bool IsWithinFailureBackoffWindow(long nowUtcTicks, long retryAfterUtcTicks) =>
        nowUtcTicks < retryAfterUtcTicks;

    /// <summary>
    /// Computes the UTC tick value when retries should resume.
    /// </summary>
    public static long ComputeRetryAfterUtcTicks(long nowUtcTicks, TimeSpan backoff) =>
        nowUtcTicks + backoff.Ticks;
}

/// <summary>
/// Result payload for stampede key validation.
/// </summary>
/// <param name="IsValid">Whether the key is accepted by runtime policy.</param>
/// <param name="Reason">Rejection reason when invalid.</param>
public readonly record struct StampedeKeyValidationResult(bool IsValid, StampedeKeyRejectionReason Reason)
{
    /// <summary>
    /// Successful validation result.
    /// </summary>
    public static StampedeKeyValidationResult Valid => new(true, StampedeKeyRejectionReason.None);

    /// <summary>
    /// Failed validation result with a concrete reason.
    /// </summary>
    public static StampedeKeyValidationResult Rejected(StampedeKeyRejectionReason reason) =>
        new(false, reason);
}

/// <summary>
/// Reason codes for stampede key validation failures.
/// </summary>
public enum StampedeKeyRejectionReason
{
    /// <summary>No rejection.</summary>
    None = 0,
    /// <summary>Key is null, empty, or whitespace.</summary>
    Empty = 1,
    /// <summary>Key length exceeds configured maximum.</summary>
    MaxLength = 2,
    /// <summary>Key contains control characters.</summary>
    ControlCharacter = 3
}
