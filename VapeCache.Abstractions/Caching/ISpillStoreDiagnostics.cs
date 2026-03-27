namespace VapeCache.Abstractions.Caching;

/// <summary>
/// Provides runtime diagnostics for in-memory spill storage behavior.
/// </summary>
public interface ISpillStoreDiagnostics
{
    /// <summary>
    /// Gets a point-in-time spill diagnostics snapshot.
    /// </summary>
    SpillStoreDiagnosticsSnapshot GetSnapshot();
}

/// <summary>
/// Runtime diagnostics payload for spill-to-disk behavior and shard distribution.
/// </summary>
public sealed record SpillStoreDiagnosticsSnapshot
{
    public SpillStoreDiagnosticsSnapshot(
        bool SupportsDiskSpill,
        bool SpillToDiskConfigured,
        string Mode,
        long TotalSpillFiles,
        int ActiveShards,
        int MaxFilesInShard,
        double AvgFilesPerActiveShard,
        double ImbalanceRatio,
        SpillShardLoad[] TopShards,
        DateTimeOffset SampledAtUtc)
    {
        this.SupportsDiskSpill = SupportsDiskSpill;
        this.SpillToDiskConfigured = SpillToDiskConfigured;
        this.Mode = Mode;
        this.TotalSpillFiles = TotalSpillFiles;
        this.ActiveShards = ActiveShards;
        this.MaxFilesInShard = MaxFilesInShard;
        this.AvgFilesPerActiveShard = AvgFilesPerActiveShard;
        this.ImbalanceRatio = ImbalanceRatio;
        this.TopShards = TopShards;
        this.SampledAtUtc = SampledAtUtc;
    }

    public bool SupportsDiskSpill { get; init; }
    public bool SpillToDiskConfigured { get; init; }
    public string Mode { get; init; }
    public long TotalSpillFiles { get; init; }
    public int ActiveShards { get; init; }
    public int MaxFilesInShard { get; init; }
    public double AvgFilesPerActiveShard { get; init; }
    public double ImbalanceRatio { get; init; }
    public SpillShardLoad[] TopShards { get; init; }
    public DateTimeOffset SampledAtUtc { get; init; }
}

/// <summary>
/// Per-shard load sample for spill diagnostics.
/// </summary>
public sealed record SpillShardLoad
{
    public SpillShardLoad(string Shard, int FileCount)
    {
        this.Shard = Shard;
        this.FileCount = FileCount;
    }

    public string Shard { get; init; }
    public int FileCount { get; init; }
}
