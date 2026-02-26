using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VapeCache.Licensing.ControlPlane.Revocation;

namespace VapeCache.Tests.Licensing;

public sealed class RevocationRegistryTests
{
    [Fact]
    public void RevokeAndActivateLicense_UpdatesStatus()
    {
        var statePath = CreateTempStatePath();
        try
        {
            var registry = CreateRegistry(statePath);
            registry.RevokeLicense("lic-123", "fraud-detected", "ops");

            var revoked = registry.Evaluate("lic-123", organizationId: null, keyId: null);
            Assert.True(revoked.Revoked);
            Assert.Equal("license", revoked.Source);
            Assert.Equal("fraud-detected", revoked.Reason);

            registry.ActivateLicense("lic-123", "manual-restore", "ops");
            var active = registry.Evaluate("lic-123", organizationId: null, keyId: null);
            Assert.False(active.Revoked);
            Assert.Equal("active", active.Reason);
        }
        finally
        {
            CleanupTempStatePath(statePath);
        }
    }

    [Fact]
    public void Evaluate_PrefersLicenseOverOrganizationAndKey()
    {
        var statePath = CreateTempStatePath();
        try
        {
            var registry = CreateRegistry(statePath);
            registry.EnableOrganizationKillSwitch("org-1", "organization-killed", "ops");
            registry.RevokeKeyId("kid-1", "key-revoked", "ops");
            registry.RevokeLicense("lic-1", "license-revoked", "ops");

            var decision = registry.Evaluate("lic-1", "org-1", "kid-1");
            Assert.True(decision.Revoked);
            Assert.Equal("license", decision.Source);
            Assert.Equal("license-revoked", decision.Reason);
        }
        finally
        {
            CleanupTempStatePath(statePath);
        }
    }

    [Fact]
    public void StateFile_RoundTripsAcrossRegistryInstances()
    {
        var statePath = CreateTempStatePath();
        try
        {
            var registryA = CreateRegistry(statePath);
            registryA.RevokeLicense("lic-roundtrip", "compromised", "ops");
            registryA.EnableOrganizationKillSwitch("org-roundtrip", "org-kill", "ops");

            var registryB = CreateRegistry(statePath);
            var licenseDecision = registryB.Evaluate("lic-roundtrip", organizationId: null, keyId: null);
            var orgDecision = registryB.Evaluate("non-revoked-license", "org-roundtrip", keyId: null);

            Assert.True(licenseDecision.Revoked);
            Assert.Equal("license", licenseDecision.Source);
            Assert.True(orgDecision.Revoked);
            Assert.Equal("organization", orgDecision.Source);
        }
        finally
        {
            CleanupTempStatePath(statePath);
        }
    }

    private static FileBackedRevocationRegistry CreateRegistry(string statePath)
    {
        var options = new RevocationControlPlaneOptions
        {
            PersistencePath = statePath,
            RequireApiKey = false
        };

        return new FileBackedRevocationRegistry(
            new StaticOptionsMonitor<RevocationControlPlaneOptions>(options),
            NullLogger<FileBackedRevocationRegistry>.Instance);
    }

    private static string CreateTempStatePath()
    {
        var folder = Path.Combine(Path.GetTempPath(), "vapecache-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, "revocations-state.json");
    }

    private static void CleanupTempStatePath(string statePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(statePath);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // best-effort test cleanup
        }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable OnChange(Action<T, string?> listener) => EmptyDisposable.Instance;
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose()
        {
        }
    }
}
