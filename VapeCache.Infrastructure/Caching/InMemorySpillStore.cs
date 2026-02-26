using VapeCache.Abstractions.Caching;
using Microsoft.Extensions.Options;

namespace VapeCache.Infrastructure.Caching;

/// <summary>
/// No-op spill store for free tier - spill-to-disk is disabled.
/// For Enterprise spill-to-disk functionality with scatter/gather distribution
/// and encryption at rest, install VapeCache.Persistence package.
/// </summary>
internal sealed class NoopSpillStore(
    IOptionsMonitor<InMemorySpillOptions>? optionsMonitor = null) : IInMemorySpillStore, ISpillStoreDiagnostics
{
    private readonly IOptionsMonitor<InMemorySpillOptions>? _optionsMonitor = optionsMonitor;

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask WriteAsync(Guid spillRef, ReadOnlyMemory<byte> data, CancellationToken ct)
        => ValueTask.CompletedTask;

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public ValueTask<byte[]?> TryReadAsync(Guid spillRef, CancellationToken ct)
        => ValueTask.FromResult<byte[]?>(null);

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask DeleteAsync(Guid spillRef, CancellationToken ct)
        => ValueTask.CompletedTask;

    /// <summary>
    /// Gets value.
    /// </summary>
    public SpillStoreDiagnosticsSnapshot GetSnapshot()
    {
        var configured = _optionsMonitor?.CurrentValue.EnableSpillToDisk ?? false;
        return new SpillStoreDiagnosticsSnapshot(
            SupportsDiskSpill: false,
            SpillToDiskConfigured: configured,
            Mode: "noop",
            TotalSpillFiles: 0,
            ActiveShards: 0,
            MaxFilesInShard: 0,
            AvgFilesPerActiveShard: 0d,
            ImbalanceRatio: 0d,
            TopShards: Array.Empty<SpillShardLoad>(),
            SampledAtUtc: DateTimeOffset.UtcNow);
    }
}
