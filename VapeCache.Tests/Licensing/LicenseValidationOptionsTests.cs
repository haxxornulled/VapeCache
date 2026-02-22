using VapeCache.Licensing;

namespace VapeCache.Tests.Licensing;

public class LicenseValidationOptionsTests
{
    [Fact]
    public void ResolveValidationSecret_NoOverride_ReturnsDefault()
    {
        lock (LicenseTestEnvironment.EnvironmentLock)
        {
            var original = Environment.GetEnvironmentVariable(LicenseValidationOptions.ValidationSecretEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.ValidationSecretEnvironmentVariable, null);

                var resolved = LicenseValidationOptions.ResolveValidationSecret();

                Assert.Equal(LicenseValidationOptions.DefaultValidationSecret, resolved);
            }
            finally
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.ValidationSecretEnvironmentVariable, original);
            }
        }
    }

    [Fact]
    public void ResolveValidationSecret_WithOverride_ReturnsOverride()
    {
        const string customSecret = "override-secret-2026";

        lock (LicenseTestEnvironment.EnvironmentLock)
        {
            var original = Environment.GetEnvironmentVariable(LicenseValidationOptions.ValidationSecretEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.ValidationSecretEnvironmentVariable, customSecret);

                var resolved = LicenseValidationOptions.ResolveValidationSecret();

                Assert.Equal(customSecret, resolved);
            }
            finally
            {
                Environment.SetEnvironmentVariable(LicenseValidationOptions.ValidationSecretEnvironmentVariable, original);
            }
        }
    }
}
