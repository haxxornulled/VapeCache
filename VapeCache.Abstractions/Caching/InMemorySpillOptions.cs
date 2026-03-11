namespace VapeCache.Abstractions.Caching;

/// <summary>
/// Represents the n memory spill options.
/// </summary>
public sealed class InMemorySpillOptions
{
    /// <summary>
    /// Gets or sets the enable spill to disk.
    /// </summary>
    public bool EnableSpillToDisk { get; set; }
    /// <summary>
    /// Gets or sets the spill threshold bytes.
    /// </summary>
    public int SpillThresholdBytes { get; set; } = 256 * 1024;
    /// <summary>
    /// Gets or sets the nline prefix bytes.
    /// </summary>
    public int InlinePrefixBytes { get; set; } = 4096;
    /// <summary>
    /// Gets or sets the spill directory.
    /// </summary>
    public string? SpillDirectory { get; set; }
    /// <summary>
    /// Gets or sets the enable orphan cleanup.
    /// </summary>
    public bool EnableOrphanCleanup { get; set; }
    /// <summary>
    /// Executes from hours.
    /// </summary>
    public TimeSpan OrphanCleanupInterval { get; set; } = TimeSpan.FromHours(1);
    /// <summary>
    /// Executes from days.
    /// </summary>
    public TimeSpan OrphanMaxAge { get; set; } = TimeSpan.FromDays(7);
}
