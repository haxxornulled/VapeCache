namespace VapeCache.Licensing;

/// <summary>
/// Shared licensing validation settings.
/// </summary>
public static class LicenseValidationOptions
{
    /// <summary>
    /// Optional override for the server-side validation secret.
    /// </summary>
    public const string ValidationSecretEnvironmentVariable = "VAPECACHE_LICENSE_VALIDATION_SECRET";

    /// <summary>
    /// Default validation secret used when the environment override is not set.
    /// </summary>
    public const string DefaultValidationSecret = "VapeCache-HMAC-Secret-2026-Production";

    /// <summary>
    /// Resolves the validation secret from environment with fallback.
    /// </summary>
    public static string ResolveValidationSecret()
    {
        var overrideValue = Environment.GetEnvironmentVariable(ValidationSecretEnvironmentVariable);
        return string.IsNullOrWhiteSpace(overrideValue)
            ? DefaultValidationSecret
            : overrideValue;
    }
}
