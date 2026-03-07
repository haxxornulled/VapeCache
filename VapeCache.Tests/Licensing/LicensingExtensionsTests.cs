using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VapeCache.Abstractions.Caching;
using VapeCache.Infrastructure.Caching;
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
            var originalAllowVerifierOverride = Environment.GetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable);

            try
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable, publicKeyPem);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable, keyId);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable, "true");

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
                Environment.SetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable, originalAllowVerifierOverride);
            }
        }
    }

    [Fact]
    public void AddVapeCachePersistence_RegistersSharedFileSpillDiagnosticsInstance()
    {
        const string keyId = "persistence-test-kid";
        var (privateKeyPem, publicKeyPem) = LicenseTestKeys.GeneratePemKeyPair();

        lock (LicenseTestEnvironment.EnvironmentLock)
        {
            var originalPublicKey = Environment.GetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable);
            var originalKeyId = Environment.GetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable);
            var originalAllowVerifierOverride = Environment.GetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable);

            try
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable, publicKeyPem);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable, keyId);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable, "true");

                var issuer = new LicenseTokenIssuer(privateKeyPem, keyId);
                var key = issuer.GenerateEnterpriseLicenseKey("acme", DateTimeOffset.UtcNow.AddDays(7));

                var services = new ServiceCollection();
                services.AddVapecacheCaching();
                services.AddVapeCachePersistence(key);
                using var provider = services.BuildServiceProvider();

                var spillStore = provider.GetRequiredService<IInMemorySpillStore>();
                var spillDiagnostics = provider.GetRequiredService<ISpillStoreDiagnostics>();

                Assert.IsType<FileSpillStore>(spillStore);
                Assert.Same(spillStore, spillDiagnostics);
            }
            finally
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable, originalPublicKey);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable, originalKeyId);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable, originalAllowVerifierOverride);
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
            var originalAllowVerifierOverride = Environment.GetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable);

            try
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable, publicKeyPem);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable, keyId);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable, "true");

                var issuer = new LicenseTokenIssuer(privateKeyPem, keyId);
                var key = issuer.GenerateEnterpriseLicenseKey(
                    "acme",
                    DateTimeOffset.UtcNow.AddDays(7),
                    features: new[] { LicenseFeatures.Reconciliation });

                var services = new ServiceCollection();
                var ex = Assert.Throws<VapeCacheLicenseException>(() => services.AddVapeCachePersistence(key));

                Assert.Contains(LicenseFeatures.Persistence, ex.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable, originalPublicKey);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable, originalKeyId);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable, originalAllowVerifierOverride);
            }
        }
    }

    [Fact]
    public void AddVapeCachePersistence_NoExplicitLicense_UsesEnvironmentVariable()
    {
        const string keyId = "persistence-env-kid";
        var (privateKeyPem, publicKeyPem) = LicenseTestKeys.GeneratePemKeyPair();

        lock (LicenseTestEnvironment.EnvironmentLock)
        {
            var originalPublicKey = Environment.GetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable);
            var originalKeyId = Environment.GetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable);
            var originalLicenseKey = Environment.GetEnvironmentVariable("VAPECACHE_LICENSE_KEY");
            var originalAllowVerifierOverride = Environment.GetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable);

            try
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable, publicKeyPem);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable, keyId);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable, "true");

                var issuer = new LicenseTokenIssuer(privateKeyPem, keyId);
                var key = issuer.GenerateEnterpriseLicenseKey("acme", DateTimeOffset.UtcNow.AddDays(7));
                Environment.SetEnvironmentVariable("VAPECACHE_LICENSE_KEY", key);

                var services = new ServiceCollection();
                services.AddVapeCachePersistence();

                Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IInMemorySpillStore));
            }
            finally
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable, originalPublicKey);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable, originalKeyId);
                Environment.SetEnvironmentVariable("VAPECACHE_LICENSE_KEY", originalLicenseKey);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable, originalAllowVerifierOverride);
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
            var originalAllowVerifierOverride = Environment.GetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable);

            try
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable, publicKeyPem);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable, keyId);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable, "true");
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
                Environment.SetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable, originalAllowVerifierOverride);
            }
        }
    }

    [Fact]
    public void AddVapeCacheRedisReconciliation_NoLicenseKey_ThrowsMissingKey()
    {
        lock (LicenseTestEnvironment.EnvironmentLock)
        {
            const string keyId = "reconciliation-test-kid";
            var (_, publicKeyPem) = LicenseTestKeys.GeneratePemKeyPair();
            var originalPublicKey = Environment.GetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable);
            var originalKeyId = Environment.GetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable);
            var originalLicenseKey = Environment.GetEnvironmentVariable("VAPECACHE_LICENSE_KEY");
            var originalAllowVerifierOverride = Environment.GetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable, publicKeyPem);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable, keyId);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable, "true");
                Environment.SetEnvironmentVariable("VAPECACHE_LICENSE_KEY", null);
                var services = new ServiceCollection();

                var ex = Assert.Throws<VapeCacheLicenseException>(() =>
                    services.AddVapeCacheRedisReconciliation((string?)null));

                Assert.Contains("Missing license key", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable, originalPublicKey);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable, originalKeyId);
                Environment.SetEnvironmentVariable("VAPECACHE_LICENSE_KEY", originalLicenseKey);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable, originalAllowVerifierOverride);
            }
        }
    }

    [Fact]
    public void AddReconciliationReaper_WithoutReconciliationRegistration_ThrowsHelpfulError()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddReconciliationReaper());

        Assert.Contains("AddVapeCacheRedisReconciliation", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddReconciliationReaper_WithReconciliationRegistration_RegistersHostedService()
    {
        const string keyId = "reaper-registration-kid";
        var (privateKeyPem, publicKeyPem) = LicenseTestKeys.GeneratePemKeyPair();

        lock (LicenseTestEnvironment.EnvironmentLock)
        {
            var originalPublicKey = Environment.GetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable);
            var originalKeyId = Environment.GetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable);
            var originalAllowVerifierOverride = Environment.GetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable);

            try
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable, publicKeyPem);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable, keyId);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable, "true");

                var issuer = new LicenseTokenIssuer(privateKeyPem, keyId);
                var key = issuer.GenerateEnterpriseLicenseKey("acme", DateTimeOffset.UtcNow.AddDays(7));

                var services = new ServiceCollection();
                services.AddVapeCacheRedisReconciliation(key);
                services.AddReconciliationReaper();

                Assert.Contains(
                    services,
                    descriptor =>
                        descriptor.ServiceType == typeof(IHostedService) &&
                        descriptor.ImplementationType == typeof(RedisReconciliationReaper));
            }
            finally
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable, originalPublicKey);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.VerificationKeyIdEnvironmentVariable, originalKeyId);
                Environment.SetEnvironmentVariable(LicenseValidationOptions.AllowVerificationOverrideEnvironmentVariable, originalAllowVerifierOverride);
            }
        }
    }
}
