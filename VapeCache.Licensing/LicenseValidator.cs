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
    /// Application-based licensing - no per-server/cluster counting.
    /// </summary>
    /// <param name="licenseKey">License key in format: VCENT-{ORG_ID}-{EXPIRY}-{SIGNATURE}</param>
    /// <returns>Validation result with license details or error message.</returns>
    public LicenseValidationResult Validate(string? licenseKey)
    {
        // No license = Free tier
        if (string.IsNullOrWhiteSpace(licenseKey))
            return LicenseValidationResult.Free();

        var parts = licenseKey.Split('-');
        if (parts.Length != 4)
            return LicenseValidationResult.Failure("Invalid license key format. Expected: VCENT-ORG_ID-EXPIRY-SIGNATURE");

        var tierPrefix = parts[0];
        var organizationId = parts[1];
        var expiryStr = parts[2];
        var providedSignature = parts[3];

        // Only Enterprise tier has license keys
        if (tierPrefix != "VCENT")
            return LicenseValidationResult.Failure($"Invalid license tier prefix: {tierPrefix}. Expected: VCENT");

        // Parse expiry
        if (!long.TryParse(expiryStr, out var expiryUnix))
            return LicenseValidationResult.Failure("Invalid expiry date format");

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(expiryUnix);
        if (expiresAt < DateTimeOffset.UtcNow)
            return LicenseValidationResult.Failure($"License expired on {expiresAt:yyyy-MM-dd}");

        // Verify HMAC signature
        var payload = $"{tierPrefix}-{organizationId}-{expiryStr}";
        var expectedSignature = ComputeSignature(payload);

        if (!string.Equals(providedSignature, expectedSignature, StringComparison.OrdinalIgnoreCase))
            return LicenseValidationResult.Failure("Invalid license signature");

        // Enterprise tier = unlimited instances/servers/clusters
        return LicenseValidationResult.Success(LicenseTier.Enterprise, organizationId, expiresAt, maxInstances: 999);
    }

    /// <summary>
    /// Generates an Enterprise license key for an organization.
    /// Application-based licensing - unlimited deployments/servers/clusters.
    /// </summary>
    public string GenerateLicenseKey(string organizationId, DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);

        var tierPrefix = "VCENT"; // Enterprise only
        var expiryUnix = expiresAt.ToUnixTimeSeconds();
        var payload = $"{tierPrefix}-{organizationId}-{expiryUnix}";
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
