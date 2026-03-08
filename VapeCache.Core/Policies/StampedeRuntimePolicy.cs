namespace VapeCache.Core.Policies;

/// <summary>
/// Domain policy decisions for runtime stampede protection behavior.
/// </summary>
public static class StampedeRuntimePolicy
{
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

    public static bool IsFailureBackoffConfigured(bool enabled, TimeSpan backoff) =>
        enabled && backoff > TimeSpan.Zero;

    public static bool IsWithinFailureBackoffWindow(long nowUtcTicks, long retryAfterUtcTicks) =>
        nowUtcTicks < retryAfterUtcTicks;

    public static long ComputeRetryAfterUtcTicks(long nowUtcTicks, TimeSpan backoff) =>
        nowUtcTicks + backoff.Ticks;
}

public readonly record struct StampedeKeyValidationResult(bool IsValid, StampedeKeyRejectionReason Reason)
{
    public static StampedeKeyValidationResult Valid => new(true, StampedeKeyRejectionReason.None);

    public static StampedeKeyValidationResult Rejected(StampedeKeyRejectionReason reason) =>
        new(false, reason);
}

public enum StampedeKeyRejectionReason
{
    None = 0,
    Empty = 1,
    MaxLength = 2,
    ControlCharacter = 3
}
