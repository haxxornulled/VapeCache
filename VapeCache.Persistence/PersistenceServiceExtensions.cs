using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VapeCache.Abstractions.Caching;
using VapeCache.Licensing;

namespace VapeCache.Persistence;

/// <summary>
/// Represents the persistence service extensions.
/// </summary>
public static class PersistenceServiceExtensions
{
    /// <summary>
    /// Registers VapeCache persistence features using VAPECACHE_LICENSE_KEY from environment.
    /// REQUIRES ENTERPRISE LICENSE ($499/month).
    /// </summary>
    public static IServiceCollection AddVapeCachePersistence(
        this IServiceCollection services,
        Action<InMemorySpillOptions>? configure = null)
        => services.AddVapeCachePersistence(licenseKey: null, configure);

    /// <summary>
    /// Registers VapeCache persistence features (spill-to-disk).
    /// REQUIRES ENTERPRISE LICENSE ($499/month).
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="licenseKey">Enterprise license key (VC2.{header}.{payload}.{signature})</param>
    /// <param name="configure">Optional configuration</param>
    public static IServiceCollection AddVapeCachePersistence(
        this IServiceCollection services,
        string? licenseKey,
        Action<InMemorySpillOptions>? configure = null)
    {
        licenseKey ??= Environment.GetEnvironmentVariable("VAPECACHE_LICENSE_KEY");
        LicenseFeatureGate.RequireEnterpriseFeature(
            licenseKey,
            LicenseFeatures.Persistence,
            "VapeCache.Persistence",
            static message => new VapeCacheLicenseException(message));

        // Register persistence services
        if (configure != null)
            services.Configure(configure);

        services.AddSingleton<ISpillEncryptionProvider, NoopSpillEncryptionProvider>();
        services.RemoveAll<FileSpillStore>();
        services.RemoveAll<IInMemorySpillStore>();
        services.RemoveAll<ISpillStoreDiagnostics>();
        services.AddSingleton<FileSpillStore>();
        services.AddSingleton<IInMemorySpillStore>(sp => sp.GetRequiredService<FileSpillStore>());
        services.AddSingleton<ISpillStoreDiagnostics>(sp => sp.GetRequiredService<FileSpillStore>());

        return services;
    }

    /// <summary>
    /// Registers VapeCache persistence with custom encryption provider.
    /// REQUIRES ENTERPRISE LICENSE ($499/month).
    /// </summary>
    public static IServiceCollection AddVapeCachePersistence<TEncryption>(
        this IServiceCollection services,
        Action<InMemorySpillOptions>? configure = null)
        where TEncryption : class, ISpillEncryptionProvider
        => services.AddVapeCachePersistence<TEncryption>(licenseKey: null, configure);

    /// <summary>
    /// Registers VapeCache persistence with custom encryption provider.
    /// REQUIRES ENTERPRISE LICENSE ($499/month).
    /// </summary>
    public static IServiceCollection AddVapeCachePersistence<TEncryption>(
        this IServiceCollection services,
        string? licenseKey,
        Action<InMemorySpillOptions>? configure = null)
        where TEncryption : class, ISpillEncryptionProvider
    {
        licenseKey ??= Environment.GetEnvironmentVariable("VAPECACHE_LICENSE_KEY");
        LicenseFeatureGate.RequireEnterpriseFeature(
            licenseKey,
            LicenseFeatures.Persistence,
            "VapeCache.Persistence",
            static message => new VapeCacheLicenseException(message));

        // Register with custom encryption
        if (configure != null)
            services.Configure(configure);

        services.AddSingleton<ISpillEncryptionProvider, TEncryption>();
        services.RemoveAll<FileSpillStore>();
        services.RemoveAll<IInMemorySpillStore>();
        services.RemoveAll<ISpillStoreDiagnostics>();
        services.AddSingleton<FileSpillStore>();
        services.AddSingleton<IInMemorySpillStore>(sp => sp.GetRequiredService<FileSpillStore>());
        services.AddSingleton<ISpillStoreDiagnostics>(sp => sp.GetRequiredService<FileSpillStore>());

        return services;
    }
}
