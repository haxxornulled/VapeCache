using VapeCache.Licensing;

namespace VapeCache.Tests.Licensing;

public sealed class LicenseRevocationClientTests
{
#pragma warning disable xUnit1031
    [Fact]
    public void CheckAsync_WhenEndpointFails_FailsClosedByDefault()
    {
        lock (LicenseTestEnvironment.EnvironmentLock)
        {
            var originalValues = CaptureEnvironment();
            try
            {
                Environment.SetEnvironmentVariable(LicenseRevocationRuntimeOptions.RevocationEnabledEnvironmentVariable, "true");
                Environment.SetEnvironmentVariable(LicenseRevocationRuntimeOptions.RevocationEndpointEnvironmentVariable, "http://127.0.0.1:1");
                Environment.SetEnvironmentVariable(LicenseRevocationRuntimeOptions.RevocationFailOpenEnvironmentVariable, null);
                Environment.SetEnvironmentVariable(LicenseRevocationRuntimeOptions.RevocationTimeoutMsEnvironmentVariable, "50");
                Environment.SetEnvironmentVariable(LicenseRevocationRuntimeOptions.RevocationCacheSecondsEnvironmentVariable, "0");

                var client = new LicenseRevocationClient();
                var result = client.CheckAsync(CreateEnterpriseValidationResult()).AsTask().GetAwaiter().GetResult();

                Assert.False(result.IsAllowed);
                Assert.Contains("fail-closed", result.Reason, StringComparison.Ordinal);
            }
            finally
            {
                RestoreEnvironment(originalValues);
            }
        }
    }

    [Fact]
    public void CheckAsync_WhenFailOpenIsExplicitlyEnabled_AllowsOnTransportError()
    {
        lock (LicenseTestEnvironment.EnvironmentLock)
        {
            var originalValues = CaptureEnvironment();
            try
            {
                Environment.SetEnvironmentVariable(LicenseRevocationRuntimeOptions.RevocationEnabledEnvironmentVariable, "true");
                Environment.SetEnvironmentVariable(LicenseRevocationRuntimeOptions.RevocationEndpointEnvironmentVariable, "http://127.0.0.1:1");
                Environment.SetEnvironmentVariable(LicenseRevocationRuntimeOptions.RevocationFailOpenEnvironmentVariable, "true");
                Environment.SetEnvironmentVariable(LicenseRevocationRuntimeOptions.RevocationTimeoutMsEnvironmentVariable, "50");
                Environment.SetEnvironmentVariable(LicenseRevocationRuntimeOptions.RevocationCacheSecondsEnvironmentVariable, "0");

                var client = new LicenseRevocationClient();
                var result = client.CheckAsync(CreateEnterpriseValidationResult()).AsTask().GetAwaiter().GetResult();

                Assert.True(result.IsAllowed);
                Assert.Contains("fail-open", result.Reason, StringComparison.Ordinal);
            }
            finally
            {
                RestoreEnvironment(originalValues);
            }
        }
    }
#pragma warning restore xUnit1031

    private static Dictionary<string, string?> CaptureEnvironment()
        => new(StringComparer.Ordinal)
        {
            [LicenseRevocationRuntimeOptions.RevocationEnabledEnvironmentVariable] = Environment.GetEnvironmentVariable(LicenseRevocationRuntimeOptions.RevocationEnabledEnvironmentVariable),
            [LicenseRevocationRuntimeOptions.RevocationEndpointEnvironmentVariable] = Environment.GetEnvironmentVariable(LicenseRevocationRuntimeOptions.RevocationEndpointEnvironmentVariable),
            [LicenseRevocationRuntimeOptions.RevocationApiKeyEnvironmentVariable] = Environment.GetEnvironmentVariable(LicenseRevocationRuntimeOptions.RevocationApiKeyEnvironmentVariable),
            [LicenseRevocationRuntimeOptions.RevocationFailOpenEnvironmentVariable] = Environment.GetEnvironmentVariable(LicenseRevocationRuntimeOptions.RevocationFailOpenEnvironmentVariable),
            [LicenseRevocationRuntimeOptions.RevocationTimeoutMsEnvironmentVariable] = Environment.GetEnvironmentVariable(LicenseRevocationRuntimeOptions.RevocationTimeoutMsEnvironmentVariable),
            [LicenseRevocationRuntimeOptions.RevocationCacheSecondsEnvironmentVariable] = Environment.GetEnvironmentVariable(LicenseRevocationRuntimeOptions.RevocationCacheSecondsEnvironmentVariable)
        };

    private static void RestoreEnvironment(IReadOnlyDictionary<string, string?> values)
    {
        foreach (var entry in values)
            Environment.SetEnvironmentVariable(entry.Key, entry.Value);
    }

    private static LicenseValidationResult CreateEnterpriseValidationResult()
        => LicenseValidationResult.Success(
            LicenseTier.Enterprise,
            customerId: "acme",
            expiresAt: DateTimeOffset.UtcNow.AddDays(7),
            maxInstances: 999,
            keyId: "kid-2026",
            licenseId: "license-2026",
            features: [LicenseFeatures.Persistence]);
}
