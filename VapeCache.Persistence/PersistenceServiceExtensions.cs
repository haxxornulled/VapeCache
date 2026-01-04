using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Licensing;

namespace VapeCache.Persistence;

public static class PersistenceServiceExtensions
{
    /// <summary>
    /// Registers VapeCache persistence features (spill-to-disk).
    /// REQUIRES ENTERPRISE LICENSE ($499/month).
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="licenseKey">Enterprise license key (VCENT-...)</param>
    /// <param name="configure">Optional configuration</param>
    public static IServiceCollection AddVapeCachePersistence(
        this IServiceCollection services,
        string licenseKey,
        Action<InMemorySpillOptions>? configure = null)
    {
        // Validate Enterprise license
        var validator = new LicenseValidator(LicenseConstants.SecretKey);
        var result = validator.Validate(licenseKey);

        if (!result.IsValid)
            throw new InvalidOperationException($"Invalid VapeCache license: {result.ErrorMessage}");

        if (result.Tier != LicenseTier.Enterprise)
            throw new InvalidOperationException(
                $"VapeCache.Persistence requires Enterprise tier. Current tier: {result.Tier}. " +
                "Upgrade at https://vapecache.com/pricing");

        if (result.IsExpired)
            throw new InvalidOperationException(
                $"VapeCache license expired on {result.ExpiresAt:yyyy-MM-dd}. " +
                "Renew at https://vapecache.com/account");

        // Register persistence services
        if (configure != null)
            services.Configure(configure);

        services.AddSingleton<ISpillEncryptionProvider, NoopSpillEncryptionProvider>();
        services.AddSingleton<IInMemorySpillStore, FileSpillStore>();

        return services;
    }

    /// <summary>
    /// Registers VapeCache persistence with custom encryption provider.
    /// REQUIRES ENTERPRISE LICENSE ($499/month).
    /// </summary>
    public static IServiceCollection AddVapeCachePersistence<TEncryption>(
        this IServiceCollection services,
        string licenseKey,
        Action<InMemorySpillOptions>? configure = null)
        where TEncryption : class, ISpillEncryptionProvider
    {
        // Validate license (same as above)
        var validator = new LicenseValidator(LicenseConstants.SecretKey);
        var result = validator.Validate(licenseKey);

        if (!result.IsValid)
            throw new InvalidOperationException($"Invalid VapeCache license: {result.ErrorMessage}");

        if (result.Tier != LicenseTier.Enterprise)
            throw new InvalidOperationException(
                $"VapeCache.Persistence requires Enterprise tier. Current tier: {result.Tier}");

        if (result.IsExpired)
            throw new InvalidOperationException($"VapeCache license expired on {result.ExpiresAt:yyyy-MM-dd}");

        // Register with custom encryption
        if (configure != null)
            services.Configure(configure);

        services.AddSingleton<ISpillEncryptionProvider, TEncryption>();
        services.AddSingleton<IInMemorySpillStore, FileSpillStore>();

        return services;
    }
}

/// <summary>
/// Internal constants for license validation.
/// Matches the secret key in LicenseGenerator.
/// </summary>
internal static class LicenseConstants
{
    internal const string SecretKey = "VapeCache-HMAC-Secret-2026-Production";
}
