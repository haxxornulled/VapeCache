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
public sealed record SpillStoreDiagnosticsSnapshot(
    bool SupportsDiskSpill,
    bool SpillToDiskConfigured,
    string Mode,
    long TotalSpillFiles,
    int ActiveShards,
    int MaxFilesInShard,
    double AvgFilesPerActiveShard,
    double ImbalanceRatio,
    SpillShardLoad[] TopShards,
    DateTimeOffset SampledAtUtc);

/// <summary>
/// Per-shard load sample for spill diagnostics.
/// </summary>
public sealed record SpillShardLoad(
    string Shard,
    int FileCount);
