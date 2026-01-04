using System.Security.Cryptography;
using System.Text;

namespace VapeCache.Licensing;

/// <summary>
/// Validates VapeCache license keys using HMAC-SHA256 signature verification.
/// </summary>
public sealed class LicenseValidator
{
    private readonly byte[] _secretKey;

    /// <summary>
    /// Creates a new license validator.
    /// </summary>
    /// <param name="secretKey">Secret key for HMAC signature verification (must match key used during generation).</param>
    public LicenseValidator(string secretKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretKey);
        _secretKey = Encoding.UTF8.GetBytes(secretKey);
    }

    /// <summary>
    /// Validates a VapeCache license key.
    /// </summary>
    /// <param name="licenseKey">License key in format: VCPRO-{CUSTOMER_ID}-{EXPIRY}-{INSTANCES}-{SIGNATURE}</param>
    /// <returns>Validation result with license details or error message.</returns>
    public LicenseValidationResult Validate(string? licenseKey)
    {
        // No license = Free tier
        if (string.IsNullOrWhiteSpace(licenseKey))
            return LicenseValidationResult.Free();

        var parts = licenseKey.Split('-');
        if (parts.Length != 5)
            return LicenseValidationResult.Failure("Invalid license key format. Expected: VCTIER-ID-EXPIRY-INSTANCES-SIGNATURE");

        var tierPrefix = parts[0];
        var customerId = parts[1];
        var expiryStr = parts[2];
        var instancesStr = parts[3];
        var providedSignature = parts[4];

        // Parse tier
        LicenseTier tier;
        if (tierPrefix == "VCPRO")
            tier = LicenseTier.Pro;
        else if (tierPrefix == "VCENT")
            tier = LicenseTier.Enterprise;
        else
            return LicenseValidationResult.Failure($"Invalid license tier prefix: {tierPrefix}");

        // Parse expiry
        if (!long.TryParse(expiryStr, out var expiryUnix))
            return LicenseValidationResult.Failure("Invalid expiry date format");

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(expiryUnix);
        if (expiresAt < DateTimeOffset.UtcNow)
            return LicenseValidationResult.Failure($"License expired on {expiresAt:yyyy-MM-dd}");

        // Parse instances
        if (!int.TryParse(instancesStr, out var maxInstances))
            return LicenseValidationResult.Failure("Invalid instance count format");

        // Validate instance count matches tier
        if (tier == LicenseTier.Pro && maxInstances != 3)
            return LicenseValidationResult.Failure("Pro licenses must have maxInstances=3");

        if (tier == LicenseTier.Enterprise && maxInstances != 999)
            return LicenseValidationResult.Failure("Enterprise licenses must have maxInstances=999 (unlimited)");

        // Verify HMAC signature
        var payload = $"{tierPrefix}-{customerId}-{expiryStr}-{instancesStr}";
        var expectedSignature = ComputeSignature(payload);

        if (!string.Equals(providedSignature, expectedSignature, StringComparison.OrdinalIgnoreCase))
            return LicenseValidationResult.Failure("Invalid license signature");

        return LicenseValidationResult.Success(tier, customerId, expiresAt, maxInstances);
    }

    /// <summary>
    /// Generates a license key for testing or internal use.
    /// </summary>
    public string GenerateLicenseKey(LicenseTier tier, string customerId, DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);

        var tierPrefix = tier switch
        {
            LicenseTier.Pro => "VCPRO",
            LicenseTier.Enterprise => "VCENT",
            _ => throw new ArgumentException("Cannot generate license key for Free tier", nameof(tier))
        };

        var maxInstances = tier switch
        {
            LicenseTier.Pro => 3,
            LicenseTier.Enterprise => 999,
            _ => throw new ArgumentException("Invalid tier", nameof(tier))
        };

        var expiryUnix = expiresAt.ToUnixTimeSeconds();
        var payload = $"{tierPrefix}-{customerId}-{expiryUnix}-{maxInstances}";
        var signature = ComputeSignature(payload);

        return $"{payload}-{signature}";
    }

    private string ComputeSignature(string payload)
    {
        using var hmac = new HMACSHA256(_secretKey);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).Substring(0, 16); // First 16 chars for brevity
    }
}
