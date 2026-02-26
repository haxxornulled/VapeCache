using VapeCache.Licensing;

namespace VapeCache.Tests.Licensing;

public class LicenseValidationOptionsTests
{
    [Fact]
    public void ResolveVerificationPublicKeyPem_NoOverride_ReturnsDefault()
    {
        lock (LicenseTestEnvironment.EnvironmentLock)
        {
            var original = Environment.GetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable);
            var originalAllowOverride = Environment.GetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable, null);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable, null);

                var resolved = LicenseValidationOptions.ResolveVerificationPublicKeyPem();

                Assert.Equal(LicenseValidationOptions.DefaultVerificationPublicKeyPem, resolved);
            }
            finally
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable, original);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable, originalAllowOverride);
            }
        }
    }

    [Fact]
    public void ResolveVerificationPublicKeyPem_WithOverrideButNoOptIn_ReturnsDefault()
    {
        const string customPublicPem = """
-----BEGIN PUBLIC KEY-----
ABC123
-----END PUBLIC KEY-----
""";

        lock (LicenseTestEnvironment.EnvironmentLock)
        {
            var original = Environment.GetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable);
            var originalAllowOverride = Environment.GetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable, customPublicPem);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable, null);

                var resolved = LicenseValidationOptions.ResolveVerificationPublicKeyPem();

                Assert.Equal(LicenseValidationOptions.DefaultVerificationPublicKeyPem, resolved);
            }
            finally
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable, original);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable, originalAllowOverride);
            }
        }
    }

    [Fact]
    public void ResolveVerificationPublicKeyPem_WithOverrideAndOptIn_ReturnsOverride()
    {
        const string customPublicPem = """
-----BEGIN PUBLIC KEY-----
ABC123
-----END PUBLIC KEY-----
""";

        lock (LicenseTestEnvironment.EnvironmentLock)
        {
            var original = Environment.GetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable);
            var originalAllowOverride = Environment.GetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable, customPublicPem);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable, "true");

                var resolved = LicenseValidationOptions.ResolveVerificationPublicKeyPem();

                Assert.Equal(customPublicPem, resolved);
            }
            finally
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable, original);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable, originalAllowOverride);
            }
        }
    }

    [Fact]
    public void ResolveVerificationPublicKeyPem_EscapedNewlines_AreNormalized()
    {
        const string escapedPem = "-----BEGIN PUBLIC KEY-----\\nABC123\\n-----END PUBLIC KEY-----";

        lock (LicenseTestEnvironment.EnvironmentLock)
        {
            var original = Environment.GetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable);
            var originalAllowOverride = Environment.GetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable, escapedPem);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable, "1");

                var resolved = LicenseValidationOptions.ResolveVerificationPublicKeyPem();

                Assert.DoesNotContain("\\n", resolved, StringComparison.Ordinal);
                Assert.Contains("-----BEGIN PUBLIC KEY-----", resolved, StringComparison.Ordinal);
                Assert.Contains("-----END PUBLIC KEY-----", resolved, StringComparison.Ordinal);
            }
            finally
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable, original);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable, originalAllowOverride);
            }
        }
    }

    [Fact]
    public void ResolveVerificationKeyId_NoOverride_ReturnsDefault()
    {
        lock (LicenseTestEnvironment.EnvironmentLock)
        {
            var original = Environment.GetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable);
            var originalAllowOverride = Environment.GetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable, null);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable, null);

                var resolved = LicenseValidationOptions.ResolveVerificationKeyId();

                Assert.Equal(LicenseValidationOptions.DefaultVerificationKeyId, resolved);
            }
            finally
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable, original);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable, originalAllowOverride);
            }
        }
    }

    [Fact]
    public void ResolveVerificationKeyId_WithOverrideButNoOptIn_ReturnsDefault()
    {
        const string customKeyId = "kid-2026-rotated";

        lock (LicenseTestEnvironment.EnvironmentLock)
        {
            var original = Environment.GetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable);
            var originalAllowOverride = Environment.GetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable, customKeyId);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable, null);

                var resolved = LicenseValidationOptions.ResolveVerificationKeyId();

                Assert.Equal(LicenseValidationOptions.DefaultVerificationKeyId, resolved);
            }
            finally
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable, original);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable, originalAllowOverride);
            }
        }
    }

    [Fact]
    public void ResolveVerificationKeyId_WithOverrideAndOptIn_ReturnsOverride()
    {
        const string customKeyId = "kid-2026-rotated";

        lock (LicenseTestEnvironment.EnvironmentLock)
        {
            var original = Environment.GetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable);
            var originalAllowOverride = Environment.GetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable, customKeyId);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable, "true");

                var resolved = LicenseValidationOptions.ResolveVerificationKeyId();

                Assert.Equal(customKeyId, resolved);
            }
            finally
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable, original);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable, originalAllowOverride);
            }
        }
    }

    [Fact]
    public void ResolveSigningPrivateKeyPem_NoOverride_Throws()
    {
        lock (LicenseTestEnvironment.EnvironmentLock)
        {
            var original = Environment.GetEnvironmentVariable(LicenseValidationOptions.SigningPrivateKeyEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.SigningPrivateKeyEnvironmentVariable, null);

                var ex = Assert.Throws<InvalidOperationException>(() =>
                    LicenseValidationOptions.ResolveSigningPrivateKeyPem());

                Assert.Contains(LicenseValidationOptions.SigningPrivateKeyEnvironmentVariable, ex.Message, StringComparison.Ordinal);
            }
            finally
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.SigningPrivateKeyEnvironmentVariable, original);
            }
        }
    }
}
