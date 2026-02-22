using Microsoft.Extensions.DependencyInjection;
using VapeCache.Abstractions.Caching;
using VapeCache.Licensing;
using VapeCache.Persistence;
using VapeCache.Reconciliation;

namespace VapeCache.Tests.Licensing;

public class LicensingExtensionsTests
{
    [Fact]
    public void AddVapeCachePersistence_UsesValidationSecretOverride()
    {
        const string customSecret = "unit-test-secret-2026";
        const string orgId = "acme";

        lock (LicenseTestEnvironment.EnvironmentLock)
        {
            var original = Environment.GetEnvironmentVariable(LicenseValidationOptions.ValidationSecretEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.ValidationSecretEnvironmentVariable, customSecret);
                var key = new LicenseValidator(customSecret).GenerateLicenseKey(orgId, DateTimeOffset.UtcNow.AddDays(7));

                var services = new ServiceCollection();
                services.AddVapeCachePersistence(key);

                Assert.Contains(services, d => d.ServiceType == typeof(IInMemorySpillStore));
            }
            finally
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.ValidationSecretEnvironmentVariable, original);
            }
        }
    }

    [Fact]
    public void AddVapeCacheRedisReconciliation_ExpiredKey_ThrowsValidationError()
    {
        var validator = new LicenseValidator(LicenseValidationOptions.DefaultValidationSecret);
        var expiredKey = validator.GenerateLicenseKey("acme", DateTimeOffset.UtcNow.AddDays(-1));
        var services = new ServiceCollection();

        var ex = Assert.Throws<VapeCacheLicenseException>(() =>
            services.AddVapeCacheRedisReconciliation(expiredKey));

        Assert.Contains("license validation failed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expired", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddVapeCacheRedisReconciliation_NoLicenseKey_ThrowsEnterpriseOnly()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<VapeCacheLicenseException>(() =>
            services.AddVapeCacheRedisReconciliation((string?)null));

        Assert.Contains("ENTERPRISE-ONLY", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
