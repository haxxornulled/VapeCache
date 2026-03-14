namespace VapeCache.Extensions.EntityFrameworkCore;

/// <summary>
/// Configuration for EF Core second-level cache interceptor wiring.
/// </summary>
public sealed class EfCoreSecondLevelCacheOptions
{
    /// <summary>
    /// Enables interceptor behavior.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Enables command-key diagnostics logging.
    /// </summary>
    public bool EnableCommandKeyDiagnostics { get; set; }

    /// <summary>
    /// Enables observer callback dispatch for profiler integrations.
    /// </summary>
    public bool EnableObserverCallbacks { get; set; }

    /// <summary>
    /// Enables save-changes invalidation bridge behavior.
    /// </summary>
    public bool EnableSaveChangesInvalidation { get; set; } = true;

    /// <summary>
    /// Prefix used for zone invalidation keys generated from changed entities.
    /// Default zone format: {ZonePrefix}:{EntityName}.
    /// </summary>
    public string ZonePrefix { get; set; } = "ef";
}
