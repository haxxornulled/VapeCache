using VapeCache.Licensing;

namespace VapeCache.Tests.Licensing;

public class LicenseValidatorTests
{
    [Fact]
    public void Validate_NullOrWhitespace_ReturnsFreeTier()
    {
        var (_, publicKeyPem) = LicenseTestKeys.GeneratePemKeyPair();
        var validator = new LicenseValidator(publicKeyPem, "test-key-2026");

        var nullResult = validator.Validate(null);
        var emptyResult = validator.Validate("   ");

        Assert.True(nullResult.IsValid);
        Assert.Equal(LicenseTier.Free, nullResult.Tier);
        Assert.True(emptyResult.IsValid);
        Assert.Equal(LicenseTier.Free, emptyResult.Tier);
    }

    [Fact]
    public void ValidateRequired_NullOrWhitespace_ReturnsFailure()
    {
        var (_, publicKeyPem) = LicenseTestKeys.GeneratePemKeyPair();
        var validator = new LicenseValidator(publicKeyPem, "test-key-2026");

        var nullResult = validator.ValidateRequired(null, "VapeCache.Persistence");
        var emptyResult = validator.ValidateRequired("   ", "VapeCache.Persistence");

        Assert.False(nullResult.IsValid);
        Assert.False(emptyResult.IsValid);
        Assert.Contains("Missing license key", nullResult.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenerateEnterpriseLicense_ThenValidate_ReturnsEnterpriseWithClaims()
    {
        var (privateKeyPem, publicKeyPem) = LicenseTestKeys.GeneratePemKeyPair();
        var keyId = "test-key-2026";
        var validator = new LicenseValidator(publicKeyPem, keyId);
        var issuer = new LicenseTokenIssuer(privateKeyPem, keyId);
        var expiresAt = DateTimeOffset.UtcNow.AddDays(30);

        var key = issuer.GenerateEnterpriseLicenseKey("acme", expiresAt);
        var result = validator.Validate(key);

        Assert.True(result.IsValid);
        Assert.Equal(LicenseTier.Enterprise, result.Tier);
        Assert.Equal("acme", result.CustomerId);
        Assert.Equal(keyId, result.KeyId);
        Assert.False(string.IsNullOrWhiteSpace(result.LicenseId));
        Assert.NotNull(result.ExpiresAt);
        Assert.False(result.IsExpired);
        Assert.True(result.HasFeature(LicenseFeatures.Persistence));
        Assert.True(result.HasFeature(LicenseFeatures.Reconciliation));
    }

    [Fact]
    public void Validate_ExpiredLicense_ReturnsFailure()
    {
        var (privateKeyPem, publicKeyPem) = LicenseTestKeys.GeneratePemKeyPair();
        var keyId = "test-key-2026";
        var validator = new LicenseValidator(publicKeyPem, keyId);
        var issuer = new LicenseTokenIssuer(privateKeyPem, keyId);
        var now = DateTimeOffset.UtcNow;
        var key = issuer.GenerateEnterpriseLicenseKey(
            "acme",
            expiresAt: now.AddDays(-1),
            notBefore: now.AddDays(-2),
            issuedAt: now.AddDays(-2));

        var result = validator.Validate(key);

        Assert.False(result.IsValid);
        Assert.Contains("expired", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_NotYetValidLicense_ReturnsFailure()
    {
        var (privateKeyPem, publicKeyPem) = LicenseTestKeys.GeneratePemKeyPair();
        var keyId = "test-key-2026";
        var validator = new LicenseValidator(publicKeyPem, keyId);
        var issuer = new LicenseTokenIssuer(privateKeyPem, keyId);
        var now = DateTimeOffset.UtcNow;
        var key = issuer.GenerateEnterpriseLicenseKey(
            "acme",
            expiresAt: now.AddDays(2),
            notBefore: now.AddDays(1),
            issuedAt: now);

        var result = validator.Validate(key);

        Assert.False(result.IsValid);
        Assert.Contains("not valid before", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_TamperedSignature_ReturnsFailure()
    {
        var (privateKeyPem, publicKeyPem) = LicenseTestKeys.GeneratePemKeyPair();
        var keyId = "test-key-2026";
        var validator = new LicenseValidator(publicKeyPem, keyId);
        var issuer = new LicenseTokenIssuer(privateKeyPem, keyId);
        var key = issuer.GenerateEnterpriseLicenseKey("acme", DateTimeOffset.UtcNow.AddDays(1));

        var parts = key.Split('.');
        parts[3] = MutateBase64Url(parts[3]);
        var tampered = string.Join('.', parts);

        var result = validator.Validate(tampered);

        Assert.False(result.IsValid);
        Assert.Contains("signature", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_UnknownKeyId_ReturnsFailure()
    {
        var (privateKeyPem, publicKeyPem) = LicenseTestKeys.GeneratePemKeyPair();
        var issuer = new LicenseTokenIssuer(privateKeyPem, "kid-a");
        var validator = new LicenseValidator(publicKeyPem, "kid-b");
        var key = issuer.GenerateEnterpriseLicenseKey("acme", DateTimeOffset.UtcNow.AddDays(1));

        var result = validator.Validate(key);

        Assert.False(result.IsValid);
        Assert.Contains("Unknown license key id", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_InvalidLegacyFormat_ReturnsFailure()
    {
        var (_, publicKeyPem) = LicenseTestKeys.GeneratePemKeyPair();
        var validator = new LicenseValidator(publicKeyPem, "test-key-2026");

        var result = validator.Validate("VCENT-acme-1735689600-A1B2C3D4");

        Assert.False(result.IsValid);
        Assert.Contains("Invalid license key format", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenerateEnterpriseLicense_WithDistinctFeatures_ProducesExpectedEntitlements()
    {
        var (privateKeyPem, publicKeyPem) = LicenseTestKeys.GeneratePemKeyPair();
        var keyId = "test-key-2026";
        var validator = new LicenseValidator(publicKeyPem, keyId);
        var issuer = new LicenseTokenIssuer(privateKeyPem, keyId);
        var key = issuer.GenerateEnterpriseLicenseKey(
            "acme",
            DateTimeOffset.UtcNow.AddDays(1),
            features: new[] { "  persistence", "PERSISTENCE", "reconciliation " });

        var result = validator.Validate(key);

        Assert.True(result.IsValid);
        Assert.Equal(2, result.Features.Count);
        Assert.True(result.HasFeature(LicenseFeatures.Persistence));
        Assert.True(result.HasFeature(LicenseFeatures.Reconciliation));
    }

    private static string MutateBase64Url(string input)
    {
        if (!TryFromBase64Url(input, out var bytes) || bytes.Length == 0)
            throw new InvalidOperationException("Unable to decode base64url segment for mutation.");

        bytes[0] ^= 0x01;
        return ToBase64Url(bytes);
    }

    private static string ToBase64Url(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool TryFromBase64Url(string value, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Replace('-', '+').Replace('_', '/');
        var remainder = normalized.Length % 4;
        if (remainder == 1)
            return false;

        if (remainder > 0)
            normalized = normalized.PadRight(normalized.Length + (4 - remainder), '=');

        try
        {
            bytes = Convert.FromBase64String(normalized);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
