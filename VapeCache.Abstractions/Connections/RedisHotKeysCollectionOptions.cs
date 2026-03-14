namespace VapeCache.Abstractions.Connections;

/// <summary>
/// Hotkeys collection options for Redis 8.6 HOTKEYS command.
/// </summary>
public sealed record RedisHotKeysCollectionOptions
{
    /// <summary>
    /// Track CPU-heavy keys.
    /// </summary>
    public bool IncludeCpu { get; init; } = true;

    /// <summary>
    /// Track network-heavy keys.
    /// </summary>
    public bool IncludeNet { get; init; } = true;

    /// <summary>
    /// Top-k keys to return for each metric.
    /// </summary>
    public int TopK { get; init; } = 20;

    /// <summary>
    /// Collection duration.
    /// </summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Command sampling ratio. 1 means no sampling.
    /// </summary>
    public int SampleRatio { get; init; } = 1;

    /// <summary>
    /// Optional hash-slot filter.
    /// </summary>
    public long[]? Slots { get; init; }
}
