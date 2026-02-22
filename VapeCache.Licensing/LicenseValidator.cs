using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VapeCache.Licensing;

/// <summary>
/// Validates VapeCache license keys using ECDSA (ES256) signatures.
/// </summary>
public sealed class LicenseValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new();
    private readonly string _verificationPublicKeyPem;
    private readonly string _expectedKeyId;

    /// <summary>
    /// Creates a validator from configured verification settings.
    /// </summary>
    public LicenseValidator()
        : this(
            LicenseValidationOptions.ResolveVerificationPublicKeyPem(),
            LicenseValidationOptions.ResolveVerificationKeyId())
    {
    }

    /// <summary>
    /// Creates a validator from explicit PEM public key and key id.
    /// </summary>
    public LicenseValidator(string verificationPublicKeyPem, string expectedKeyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verificationPublicKeyPem);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedKeyId);

        _verificationPublicKeyPem = verificationPublicKeyPem;
        _expectedKeyId = expectedKeyId.Trim();

        EnsureValidPublicKeyPem(_verificationPublicKeyPem);
    }

    /// <summary>
    /// Validates a VapeCache license key.
    /// Token format: VC2.{header}.{payload}.{signature}
    /// </summary>
    public LicenseValidationResult Validate(string? licenseKey)
    {
        if (string.IsNullOrWhiteSpace(licenseKey))
            return LicenseValidationResult.Free();

        if (!TrySplitToken(licenseKey, out var headerPart, out var payloadPart, out var signaturePart))
        {
            return LicenseValidationResult.Failure(
                "Invalid license key format. Expected: VC2.{header}.{payload}.{signature}");
        }

        if (!TryDeserializePart(headerPart, out LicenseTokenHeader? header) || header is null)
            return LicenseValidationResult.Failure("Invalid license header");

        if (!TryDeserializePart(payloadPart, out LicenseTokenPayload? payload) || payload is null)
            return LicenseValidationResult.Failure("Invalid license payload");

        if (!IsHeaderValid(header))
            return LicenseValidationResult.Failure("Invalid license header metadata");

        if (!string.Equals(header.KeyId, _expectedKeyId, StringComparison.Ordinal))
            return LicenseValidationResult.Failure($"Unknown license key id: {header.KeyId}");

        if (!LicenseTokenEncoding.TryFromBase64Url(signaturePart, out var signatureBytes) || signatureBytes.Length == 0)
            return LicenseValidationResult.Failure("Invalid license signature encoding");

        var signingInput = $"{headerPart}.{payloadPart}";
        if (!IsSignatureValid(signingInput, signatureBytes))
            return LicenseValidationResult.Failure("Invalid license signature");

        return ValidateClaims(header, payload);
    }

    private static bool TrySplitToken(
        string licenseKey,
        out string headerPart,
        out string payloadPart,
        out string signaturePart)
    {
        headerPart = string.Empty;
        payloadPart = string.Empty;
        signaturePart = string.Empty;

        var parts = licenseKey.Split('.');
        if (parts.Length != 4)
            return false;

        if (!string.Equals(parts[0], LicenseTokenFormat.TokenPrefix, StringComparison.Ordinal))
            return false;

        headerPart = parts[1];
        payloadPart = parts[2];
        signaturePart = parts[3];
        return true;
    }

    private static bool TryDeserializePart<T>(string part, out T? model) where T : class
    {
        model = null;

        if (!LicenseTokenEncoding.TryFromBase64Url(part, out var bytes) || bytes.Length == 0)
            return false;

        try
        {
            model = JsonSerializer.Deserialize<T>(bytes, JsonOptions);
            return model is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsHeaderValid(LicenseTokenHeader header)
    {
        return string.Equals(header.TokenType, LicenseTokenFormat.TokenType, StringComparison.Ordinal) &&
               string.Equals(header.Algorithm, LicenseTokenFormat.Algorithm, StringComparison.Ordinal) &&
               !string.IsNullOrWhiteSpace(header.KeyId);
    }

    private static void EnsureValidPublicKeyPem(string publicKeyPem)
    {
        try
        {
            using var verificationKey = ECDsa.Create();
            verificationKey.ImportFromPem(publicKeyPem);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("Invalid verification public key PEM configuration.", ex);
        }
    }

    private bool IsSignatureValid(string signingInput, byte[] signatureBytes)
    {
        try
        {
            using var verificationKey = ECDsa.Create();
            verificationKey.ImportFromPem(_verificationPublicKeyPem);
            return verificationKey.VerifyData(
                Encoding.UTF8.GetBytes(signingInput),
                signatureBytes,
                HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static LicenseValidationResult ValidateClaims(LicenseTokenHeader header, LicenseTokenPayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.OrganizationId))
            return LicenseValidationResult.Failure("Invalid organization ID");

        if (!TryParseTier(payload.Tier, out var tier))
            return LicenseValidationResult.Failure($"Invalid license tier: {payload.Tier}");

        if (!TryToUtc(payload.IssuedAtUnixSeconds, out var issuedAt))
            return LicenseValidationResult.Failure("Invalid issued-at timestamp");

        if (!TryToUtc(payload.NotBeforeUnixSeconds, out var notBefore))
            return LicenseValidationResult.Failure("Invalid not-before timestamp");

        if (!TryToUtc(payload.ExpiresAtUnixSeconds, out var expiresAt))
            return LicenseValidationResult.Failure("Invalid expiry timestamp");

        if (expiresAt <= notBefore)
            return LicenseValidationResult.Failure("Invalid license window: exp must be greater than nbf");

        if (issuedAt > expiresAt)
            return LicenseValidationResult.Failure("Invalid license window: iat cannot be after exp");

        if (string.IsNullOrWhiteSpace(payload.LicenseId))
            return LicenseValidationResult.Failure("Invalid token id (jti)");

        var normalizedFeatures = NormalizeFeatures(payload.Features);
        if (normalizedFeatures.Count == 0)
            return LicenseValidationResult.Failure("License is missing feature claims");

        var now = DateTimeOffset.UtcNow;
        if (now < notBefore)
            return LicenseValidationResult.Failure($"License is not valid before {notBefore:yyyy-MM-ddTHH:mm:ssZ}");

        if (now >= expiresAt)
            return LicenseValidationResult.Failure($"License expired on {expiresAt:yyyy-MM-dd}");

        var maxInstances = tier == LicenseTier.Enterprise ? 999 : 0;

        return LicenseValidationResult.Success(
            tier,
            payload.OrganizationId,
            expiresAt,
            maxInstances,
            header.KeyId,
            payload.LicenseId,
            normalizedFeatures);
    }

    private static bool TryParseTier(string value, out LicenseTier tier)
    {
        tier = LicenseTier.Free;

        if (string.Equals(value, "enterprise", StringComparison.OrdinalIgnoreCase))
        {
            tier = LicenseTier.Enterprise;
            return true;
        }

        if (string.Equals(value, "free", StringComparison.OrdinalIgnoreCase))
        {
            tier = LicenseTier.Free;
            return true;
        }

        return false;
    }

    private static bool TryToUtc(long unixSeconds, out DateTimeOffset dateTimeOffset)
    {
        try
        {
            dateTimeOffset = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            dateTimeOffset = default;
            return false;
        }
    }

    private static IReadOnlyList<string> NormalizeFeatures(string[]? features)
    {
        if (features is null || features.Length == 0)
            return Array.Empty<string>();

        var result = new List<string>(features.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var feature in features)
        {
            if (string.IsNullOrWhiteSpace(feature))
                continue;

            var normalized = feature.Trim().ToLowerInvariant();
            if (seen.Add(normalized))
                result.Add(normalized);
        }

        return result;
    }
}
