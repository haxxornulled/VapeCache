using System.Security.Cryptography;
using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace VapeCache.Licensing;

/// <summary>
/// Issues signed enterprise license keys using ECDSA (ES256).
/// Keep this in private/internal tooling only.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class LicenseTokenIssuer
{
    private static readonly JsonSerializerOptions JsonOptions = new();
    private readonly string _signingPrivateKeyPem;
    private readonly string _keyId;

    public LicenseTokenIssuer(string signingPrivateKeyPem, string keyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signingPrivateKeyPem);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);

        _signingPrivateKeyPem = signingPrivateKeyPem;
        _keyId = keyId.Trim();

        EnsureValidSigningKeyPem(_signingPrivateKeyPem);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public string GenerateEnterpriseLicenseKey(
        string organizationId,
        DateTimeOffset expiresAt,
        IEnumerable<string>? features = null,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? issuedAt = null,
        string? licenseId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);

        var now = DateTimeOffset.UtcNow;
        var effectiveIssuedAt = issuedAt ?? now;
        var effectiveNotBefore = notBefore ?? effectiveIssuedAt;

        if (expiresAt <= effectiveNotBefore)
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "Expiry must be later than not-before.");

        if (effectiveIssuedAt > expiresAt)
            throw new ArgumentOutOfRangeException(nameof(issuedAt), "Issued-at cannot be later than expiry.");

        var normalizedFeatures = NormalizeFeatures(features);
        if (normalizedFeatures.Count == 0)
        {
            normalizedFeatures = new List<string>
            {
                LicenseFeatures.Persistence,
                LicenseFeatures.Reconciliation
            };
        }

        var payload = new LicenseTokenPayload
        {
            OrganizationId = organizationId.Trim(),
            Tier = "enterprise",
            Features = normalizedFeatures.ToArray(),
            IssuedAtUnixSeconds = effectiveIssuedAt.ToUnixTimeSeconds(),
            NotBeforeUnixSeconds = effectiveNotBefore.ToUnixTimeSeconds(),
            ExpiresAtUnixSeconds = expiresAt.ToUnixTimeSeconds(),
            LicenseId = string.IsNullOrWhiteSpace(licenseId) ? Guid.NewGuid().ToString("N") : licenseId.Trim()
        };

        var header = new LicenseTokenHeader
        {
            Algorithm = LicenseTokenFormat.Algorithm,
            TokenType = LicenseTokenFormat.TokenType,
            KeyId = _keyId
        };

        var headerJson = JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions);
        var payloadJson = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var headerPart = LicenseTokenEncoding.ToBase64Url(headerJson);
        var payloadPart = LicenseTokenEncoding.ToBase64Url(payloadJson);
        var signingInput = $"{headerPart}.{payloadPart}";
        var signaturePart = LicenseTokenEncoding.ToBase64Url(Sign(signingInput));

        return $"{LicenseTokenFormat.TokenPrefix}.{headerPart}.{payloadPart}.{signaturePart}";
    }

    private byte[] Sign(string signingInput)
    {
        using var signingKey = ECDsa.Create();
        signingKey.ImportFromPem(_signingPrivateKeyPem);

        return signingKey.SignData(
            Encoding.UTF8.GetBytes(signingInput),
            HashAlgorithmName.SHA256);
    }

    private static void EnsureValidSigningKeyPem(string signingPrivateKeyPem)
    {
        try
        {
            using var signingKey = ECDsa.Create();
            signingKey.ImportFromPem(signingPrivateKeyPem);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("Invalid signing private key PEM configuration.", ex);
        }
    }

    private static List<string> NormalizeFeatures(IEnumerable<string>? features)
    {
        if (features is null)
            return new List<string>();

        var result = new List<string>();
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
