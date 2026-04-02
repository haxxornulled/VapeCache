namespace VapeCache.Features.Search;

/// <summary>
/// Global options for the search feature package.
/// </summary>
public sealed class VapeCacheSearchOptions
{
    /// <summary>
    /// Default configuration section name.
    /// </summary>
    public const string ConfigurationSectionName = "VapeCache:Search";

    /// <summary>
    /// Enables the package services.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Throws when RediSearch is unavailable instead of returning empty/no-op behavior.
    /// </summary>
    public bool RequireModuleAvailability { get; set; }

    /// <summary>
    /// Default result count when a query omits an explicit limit.
    /// </summary>
    public int DefaultResultCount { get; set; } = 20;
}
