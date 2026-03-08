namespace VapeCache.Abstractions.Caching;

/// <summary>
/// Interface for in-memory cache spill-to-disk storage.
/// Free tier uses NoopSpillStore (no persistence).
/// Enterprise tier uses FileSpillStore (scatter/gather persistence with encryption).
/// </summary>
public interface IInMemorySpillStore
{
    /// <summary>
    /// Executes write async.
    /// </summary>
    ValueTask WriteAsync(Guid spillRef, ReadOnlyMemory<byte> data, CancellationToken ct);
    /// <summary>
    /// Executes try read async.
    /// </summary>
    ValueTask<byte[]?> TryReadAsync(Guid spillRef, CancellationToken ct);
    /// <summary>
    /// Executes delete async.
    /// </summary>
    ValueTask DeleteAsync(Guid spillRef, CancellationToken ct);
}
