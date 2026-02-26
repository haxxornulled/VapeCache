using VapeCache.Abstractions.Caching;

namespace VapeCache.Infrastructure.Caching;

/// <summary>
/// No-op spill store for free tier - spill-to-disk is disabled.
/// For Enterprise spill-to-disk functionality with scatter/gather distribution
/// and encryption at rest, install VapeCache.Persistence package.
/// </summary>
internal sealed class NoopSpillStore : IInMemorySpillStore
{
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
}
