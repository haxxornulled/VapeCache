namespace VapeCache.Abstractions.Caching;

/// <summary>
/// Interface for in-memory cache spill-to-disk storage.
/// Free tier uses NoopSpillStore (no persistence).
/// Enterprise tier uses FileSpillStore (scatter/gather persistence with encryption).
/// </summary>
public interface IInMemorySpillStore
{
    ValueTask WriteAsync(Guid spillRef, ReadOnlyMemory<byte> data, CancellationToken ct);
    ValueTask<byte[]?> TryReadAsync(Guid spillRef, CancellationToken ct);
    ValueTask DeleteAsync(Guid spillRef, CancellationToken ct);
}
