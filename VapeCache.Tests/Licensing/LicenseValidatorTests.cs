using VapeCache.Licensing;

namespace VapeCache.Tests.Licensing;

public class LicenseValidatorTests
{
    [Fact]
    public void Validate_NullOrWhitespace_ReturnsFreeTier()
    {
        var validator = new LicenseValidator(LicenseValidationOptions.DefaultValidationSecret);

        var nullResult = validator.Validate(null);
        var emptyResult = validator.Validate("   ");

        Assert.True(nullResult.IsValid);
        Assert.Equal(LicenseTier.Free, nullResult.Tier);
        Assert.True(emptyResult.IsValid);
        Assert.Equal(LicenseTier.Free, emptyResult.Tier);
    }

    [Fact]
    public void GenerateLicenseKey_ThenValidate_ReturnsEnterprise()
    {
        var validator = new LicenseValidator(LicenseValidationOptions.DefaultValidationSecret);
        var expiresAt = DateTimeOffset.UtcNow.AddDays(30);

        var key = validator.GenerateLicenseKey("acme", expiresAt);
        var result = validator.Validate(key);

        Assert.True(result.IsValid);
        Assert.Equal(LicenseTier.Enterprise, result.Tier);
        Assert.Equal("acme", result.CustomerId);
        Assert.NotNull(result.ExpiresAt);
        Assert.False(result.IsExpired);
    }

    [Fact]
    public void Validate_ExpiredLicense_ReturnsFailure()
    {
        var validator = new LicenseValidator(LicenseValidationOptions.DefaultValidationSecret);
        var key = validator.GenerateLicenseKey("acme", DateTimeOffset.UtcNow.AddDays(-1));

        var result = validator.Validate(key);

        Assert.False(result.IsValid);
        Assert.Contains("expired", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_TamperedSignature_ReturnsFailure()
    {
        var validator = new LicenseValidator(LicenseValidationOptions.DefaultValidationSecret);
        var key = validator.GenerateLicenseKey("acme", DateTimeOffset.UtcNow.AddDays(1));
        var tampered = key[..^1] + (key[^1] == 'A' ? "B" : "A");

        var result = validator.Validate(tampered);

        Assert.False(result.IsValid);
        Assert.Contains("signature", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_InvalidSignatureLength_ReturnsFailure()
    {
        var validator = new LicenseValidator(LicenseValidationOptions.DefaultValidationSecret);
        var key = validator.GenerateLicenseKey("acme", DateTimeOffset.UtcNow.AddDays(1));
        var shortened = key[..^2];

        var result = validator.Validate(shortened);

        Assert.False(result.IsValid);
        Assert.Contains("length", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_InvalidSignatureHex_ReturnsFailure()
    {
        var validator = new LicenseValidator(LicenseValidationOptions.DefaultValidationSecret);
        var key = validator.GenerateLicenseKey("acme", DateTimeOffset.UtcNow.AddDays(1));
        var parts = key.Split('-');
        parts[3] = "ZZZZZZZZZZZZZZZZ";
        var invalidHex = string.Join('-', parts);

        var result = validator.Validate(invalidHex);

        Assert.False(result.IsValid);
        Assert.Contains("signature", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_InvalidExpiryRange_ReturnsFailure()
    {
        var validator = new LicenseValidator(LicenseValidationOptions.DefaultValidationSecret);
        var key = $"VCENT-acme-{long.MaxValue}-AAAAAAAAAAAAAAAA";

        var result = validator.Validate(key);

        Assert.False(result.IsValid);
        Assert.Contains("range", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_EmptyOrganizationId_ReturnsFailure()
    {
        var validator = new LicenseValidator(LicenseValidationOptions.DefaultValidationSecret);
        var expiryUnix = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds();
        var key = $"VCENT--{expiryUnix}-AAAAAAAAAAAAAAAA";

        var result = validator.Validate(key);

        Assert.False(result.IsValid);
        Assert.Contains("organization", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}
