using Microsoft.Extensions.DependencyInjection;
using VapeCache.Abstractions.Caching;
using VapeCache.Licensing;
using VapeCache.Persistence;
using VapeCache.Reconciliation;

namespace VapeCache.Tests.Licensing;

public class LicensingExtensionsTests
{
    [Fact]
    public void AddVapeCachePersistence_UsesVerificationKeyOverrides()
    {
        const string orgId = "acme";
        const string keyId = "persistence-test-kid";
        var (privateKeyPem, publicKeyPem) = LicenseTestKeys.GeneratePemKeyPair();

        lock (LicenseTestEnvironment.EnvironmentLock)
        {
            var originalPublicKey = Environment.GetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable);
            var originalKeyId = Environment.GetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable);

            try
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable, publicKeyPem);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable, keyId);

                var issuer = new LicenseTokenIssuer(privateKeyPem, keyId);
                var key = issuer.GenerateEnterpriseLicenseKey(orgId, DateTimeOffset.UtcNow.AddDays(7));

                var services = new ServiceCollection();
                services.AddVapeCachePersistence(key);

                Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IInMemorySpillStore));
            }
            finally
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable, originalPublicKey);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable, originalKeyId);
            }
        }
    }

    [Fact]
    public void AddVapeCachePersistence_MissingPersistenceFeature_Throws()
    {
        const string keyId = "persistence-test-kid";
        var (privateKeyPem, publicKeyPem) = LicenseTestKeys.GeneratePemKeyPair();

        lock (LicenseTestEnvironment.EnvironmentLock)
        {
            var originalPublicKey = Environment.GetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable);
            var originalKeyId = Environment.GetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable);

            try
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable, publicKeyPem);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable, keyId);

                var issuer = new LicenseTokenIssuer(privateKeyPem, keyId);
                var key = issuer.GenerateEnterpriseLicenseKey(
                    "acme",
                    DateTimeOffset.UtcNow.AddDays(7),
                    features: new[] { LicenseFeatures.Reconciliation });

                var services = new ServiceCollection();
                var ex = Assert.Throws<InvalidOperationException>(() => services.AddVapeCachePersistence(key));

                Assert.Contains(LicenseFeatures.Persistence, ex.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable, originalPublicKey);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable, originalKeyId);
            }
        }
    }

    [Fact]
    public void AddVapeCacheRedisReconciliation_ExpiredKey_ThrowsValidationError()
    {
        const string keyId = "reconciliation-test-kid";
        var (privateKeyPem, publicKeyPem) = LicenseTestKeys.GeneratePemKeyPair();

        lock (LicenseTestEnvironment.EnvironmentLock)
        {
            var originalPublicKey = Environment.GetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable);
            var originalKeyId = Environment.GetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable);
            var originalLicenseKey = Environment.GetEnvironmentVariable("VAPECACHE_LICENSE_KEY");

            try
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable, publicKeyPem);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable, keyId);
                Environment.SetEnvironmentVariable("VAPECACHE_LICENSE_KEY", null);

                var issuer = new LicenseTokenIssuer(privateKeyPem, keyId);
                var now = DateTimeOffset.UtcNow;
                var expiredKey = issuer.GenerateEnterpriseLicenseKey(
                    "acme",
                    expiresAt: now.AddDays(-1),
                    notBefore: now.AddDays(-2),
                    issuedAt: now.AddDays(-2));
                var services = new ServiceCollection();

                var ex = Assert.Throws<VapeCacheLicenseException>(() =>
                    services.AddVapeCacheRedisReconciliation(expiredKey));

                Assert.Contains("license validation failed", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("expired", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable, originalPublicKey);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable, originalKeyId);
                Environment.SetEnvironmentVariable("VAPECACHE_LICENSE_KEY", originalLicenseKey);
            }
        }
    }

    [Fact]
    public void AddVapeCacheRedisReconciliation_NoLicenseKey_ThrowsEnterpriseOnly()
    {
        lock (LicenseTestEnvironment.EnvironmentLock)
        {
            const string keyId = "reconciliation-test-kid";
            var (_, publicKeyPem) = LicenseTestKeys.GeneratePemKeyPair();
            var originalPublicKey = Environment.GetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable);
            var originalKeyId = Environment.GetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable);
            var originalLicenseKey = Environment.GetEnvironmentVariable("VAPECACHE_LICENSE_KEY");
            try
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable, publicKeyPem);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable, keyId);
                Environment.SetEnvironmentVariable("VAPECACHE_LICENSE_KEY", null);
                var services = new ServiceCollection();

                var ex = Assert.Throws<VapeCacheLicenseException>(() =>
                    services.AddVapeCacheRedisReconciliation((string?)null));

                Assert.Contains("ENTERPRISE-ONLY", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable, originalPublicKey);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable, originalKeyId);
                Environment.SetEnvironmentVariable("VAPECACHE_LICENSE_KEY", originalLicenseKey);
            }
        }
    }
}
