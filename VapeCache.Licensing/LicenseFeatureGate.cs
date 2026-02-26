namespace VapeCache.Licensing;

/// <summary>
/// Centralized enterprise feature gate for licensing checks.
/// Ensures strict required-key validation and optional online revocation enforcement.
/// </summary>
public static class LicenseFeatureGate
{
    private static readonly LicenseRevocationClient RevocationClient = new();

    /// <summary>
    /// Validates and enforces an enterprise feature entitlement.
    /// Throws using the provided exception factory when validation fails.
    /// </summary>
    public static LicenseValidationResult RequireEnterpriseFeature(
        string? licenseKey,
        string featureName,
        string componentName,
        Func<string, Exception>? exceptionFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureName);
        ArgumentException.ThrowIfNullOrWhiteSpace(componentName);

        exceptionFactory ??= static message => new InvalidOperationException(message);

        var validator = new LicenseValidator();
        var result = validator.ValidateRequired(licenseKey, componentName);

        if (!result.IsValid)
            throw exceptionFactory($"VapeCache license validation failed: {result.ErrorMessage}");

        if (result.Tier != LicenseTier.Enterprise)
        {
            throw exceptionFactory(
                $"{componentName} requires Enterprise tier. Current tier: {result.Tier}. " +
                "Upgrade at https://vapecache.com/pricing");
        }

        if (!result.HasFeature(featureName))
        {
            throw exceptionFactory(
                $"{componentName} requires '{featureName}' entitlement in your Enterprise license. " +
                "Visit https://vapecache.com/account to update features.");
        }

        if (result.IsExpired)
        {
            throw exceptionFactory(
                $"VapeCache license expired on {result.ExpiresAt:yyyy-MM-dd}. " +
                "Renew at https://vapecache.com/account");
        }

        var revocation = RevocationClient.CheckAsync(result).AsTask().GetAwaiter().GetResult();
        if (!revocation.IsAllowed)
        {
            throw exceptionFactory(
                $"VapeCache license is revoked or kill-switched for {componentName}. Reason: {revocation.Reason}");
        }

        return result;
    }
}
