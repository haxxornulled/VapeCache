namespace VapeCache.Abstractions.Caching;

public sealed class InMemorySpillOptions
{
    public bool EnableSpillToDisk { get; set; } = false;
    public int SpillThresholdBytes { get; set; } = 256 * 1024;
    public int InlinePrefixBytes { get; set; } = 4096;
    public string? SpillDirectory { get; set; }
    public bool EnableOrphanCleanup { get; set; } = false;
    public TimeSpan OrphanCleanupInterval { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan OrphanMaxAge { get; set; } = TimeSpan.FromDays(7);
}
