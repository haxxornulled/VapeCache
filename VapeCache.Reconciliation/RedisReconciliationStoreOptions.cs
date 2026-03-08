namespace VapeCache.Reconciliation;

/// <summary>
/// Represents the redis reconciliation store options.
/// </summary>
public sealed class RedisReconciliationStoreOptions
{
    /// <summary>
    /// Gets or sets the use sqlite.
    /// </summary>
    public bool UseSqlite { get; set; } = true;
    /// <summary>
    /// Gets or sets the store path.
    /// </summary>
    public string? StorePath { get; set; }
    /// <summary>
    /// Gets or sets the busy timeout ms.
    /// </summary>
    public int BusyTimeoutMs { get; set; } = 1000;
    /// <summary>
    /// Gets or sets the enable pragma optimizations.
    /// </summary>
    public bool EnablePragmaOptimizations { get; set; } = true;
    /// <summary>
    /// Gets or sets the vacuum on clear.
    /// </summary>
    public bool VacuumOnClear { get; set; } = false;
}
