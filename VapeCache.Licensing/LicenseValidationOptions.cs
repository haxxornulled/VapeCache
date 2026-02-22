namespace VapeCache.Licensing;

/// <summary>
/// Shared licensing validation settings.
/// </summary>
public static class LicenseValidationOptions
{
    /// <summary>
    /// Optional override for the verifier key id (kid).
    /// </summary>
    public const string VerificationKeyIdEnvironmentVariable = "VAPECACHE_LICENSE_PUBLIC_KEY_ID";

    /// <summary>
    /// Optional override for the PEM-encoded ECDSA public key used to verify signatures.
    /// </summary>
    public const string VerificationPublicKeyEnvironmentVariable = "VAPECACHE_LICENSE_PUBLIC_KEY_PEM";

    /// <summary>
    /// PEM-encoded ECDSA private key used by the internal generator/signing service.
    /// This value is required for signing and has no default fallback.
    /// </summary>
    public const string SigningPrivateKeyEnvironmentVariable = "VAPECACHE_LICENSE_SIGNING_PRIVATE_KEY_PEM";

    /// <summary>
    /// Default key id used for verification when override is not set.
    /// </summary>
    public const string DefaultVerificationKeyId = "vc-main-2026";

    /// <summary>
    /// Default PEM-encoded ECDSA public key used for verification.
    /// This is safe to ship because public keys are non-secret.
    /// </summary>
    public const string DefaultVerificationPublicKeyPem = """
-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEN67O/sUTHjOfwgvJsU77eNqnPAKF
xVBRzeAikMZGRGb6MdjC87Hhvi1Bt2cfgBSGO7iP5+sjphWjflpBcKt6aw==
-----END PUBLIC KEY-----
""";

    /// <summary>
    /// Resolves key id from environment with fallback.
    /// </summary>
    public static string ResolveVerificationKeyId()
    {
        var overrideValue = Environment.GetEnvironmentVariable(VerificationKeyIdEnvironmentVariable);
        return string.IsNullOrWhiteSpace(overrideValue)
            ? DefaultVerificationKeyId
            : overrideValue.Trim();
    }

    /// <summary>
    /// Resolves verification public key from environment with fallback.
    /// </summary>
    public static string ResolveVerificationPublicKeyPem()
    {
        var overrideValue = Environment.GetEnvironmentVariable(VerificationPublicKeyEnvironmentVariable);
        return string.IsNullOrWhiteSpace(overrideValue)
            ? DefaultVerificationPublicKeyPem
            : NormalizePem(overrideValue);
    }

    /// <summary>
    /// Resolves signing private key from environment.
    /// </summary>
    public static string ResolveSigningPrivateKeyPem()
    {
        var overrideValue = Environment.GetEnvironmentVariable(SigningPrivateKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(overrideValue))
        {
            throw new InvalidOperationException(
                $"Missing signing key. Set {SigningPrivateKeyEnvironmentVariable} to a PEM-encoded ECDSA private key.");
        }

        return NormalizePem(overrideValue);
    }

    private static string NormalizePem(string pemValue)
    {
        var normalized = pemValue.Trim();

        if (normalized.Contains("\\n", StringComparison.Ordinal))
        {
            normalized = normalized
                .Replace("\\r", string.Empty, StringComparison.Ordinal)
                .Replace("\\n", "\n", StringComparison.Ordinal);
        }

        return normalized;
    }
}
