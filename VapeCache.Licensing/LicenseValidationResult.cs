namespace VapeCache.Licensing;

/// <summary>
/// Result of license validation.
/// </summary>
public sealed class LicenseValidationResult
{
    /// <summary>
    /// Whether the license is valid.
    /// </summary>
    public bool IsValid { get; init; }

    /// <summary>
    /// The validated license tier.
    /// </summary>
    public LicenseTier Tier { get; init; }

    /// <summary>
    /// Customer ID from the license key.
    /// </summary>
    public string? CustomerId { get; init; }

    /// <summary>
    /// License expiration date (UTC).
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Maximum allowed instances for this license.
    /// 0 = unlimited or not applicable.
    /// </summary>
    public int MaxInstances { get; init; }

    /// <summary>
    /// Error message if validation failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Whether the license is expired.
    /// </summary>
    public bool IsExpired => ExpiresAt.HasValue && ExpiresAt.Value < DateTimeOffset.UtcNow;

    public static LicenseValidationResult Success(LicenseTier tier, string customerId, DateTimeOffset expiresAt, int maxInstances)
    {
        return new LicenseValidationResult
        {
            IsValid = true,
            Tier = tier,
            CustomerId = customerId,
            ExpiresAt = expiresAt,
            MaxInstances = maxInstances
        };
    }

    public static LicenseValidationResult Failure(string errorMessage)
    {
        return new LicenseValidationResult
        {
            IsValid = false,
            Tier = LicenseTier.Free,
            ErrorMessage = errorMessage
        };
    }

    public static LicenseValidationResult Free()
    {
        return new LicenseValidationResult
        {
            IsValid = true,
            Tier = LicenseTier.Free,
            MaxInstances = 0
        };
    }
}
