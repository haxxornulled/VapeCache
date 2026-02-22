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
    /// Key id (kid) used to verify the signature.
    /// </summary>
    public string? KeyId { get; init; }

    /// <summary>
    /// Unique token id (jti) for revocation/auditing.
    /// </summary>
    public string? LicenseId { get; init; }

    /// <summary>
    /// Licensed enterprise features.
    /// </summary>
    public IReadOnlyList<string> Features { get; init; } = Array.Empty<string>();

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

    public bool HasFeature(string feature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feature);

        foreach (var candidate in Features)
        {
            if (string.Equals(candidate, feature, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static LicenseValidationResult Success(
        LicenseTier tier,
        string customerId,
        DateTimeOffset expiresAt,
        int maxInstances,
        string keyId,
        string licenseId,
        IReadOnlyList<string> features)
    {
        return new LicenseValidationResult
        {
            IsValid = true,
            Tier = tier,
            CustomerId = customerId,
            ExpiresAt = expiresAt,
            MaxInstances = maxInstances,
            KeyId = keyId,
            LicenseId = licenseId,
            Features = features
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
            MaxInstances = 0,
            Features = Array.Empty<string>()
        };
    }
}
