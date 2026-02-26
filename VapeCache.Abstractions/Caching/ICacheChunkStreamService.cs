using System.Collections.Generic;

namespace VapeCache.Abstractions.Caching;

/// <summary>
/// Provides chunked streaming APIs for large payloads (for example media segments).
/// </summary>
/// <remarks>
/// This API is designed to work with the active cache backend and hybrid failover behavior.
/// </remarks>
public interface ICacheChunkStreamService
{
    /// <summary>
    /// Writes a stream into cache as chunked payload parts plus manifest metadata.
    /// </summary>
    /// <param name="key">Logical stream key.</param>
    /// <param name="source">Readable source stream.</param>
    /// <param name="options">Cache entry options applied to chunks and manifest.</param>
    /// <param name="writeOptions">Chunking and metadata options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Manifest describing the stored chunked payload.</returns>
    ValueTask<CacheChunkStreamManifest> WriteAsync(
        string key,
        Stream source,
        CacheEntryOptions options = default,
        CacheChunkStreamWriteOptions? writeOptions = null,
        CancellationToken ct = default);

    /// <summary>
    /// Gets manifest metadata for a chunked payload.
    /// </summary>
    /// <param name="key">Logical stream key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Manifest when found; otherwise <see langword="null" />.</returns>
    ValueTask<CacheChunkStreamManifest?> GetManifestAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Reads a chunked payload as sequential chunks.
    /// </summary>
    /// <param name="key">Logical stream key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async chunk sequence; empty when key is missing.</returns>
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Copies a chunked payload directly into a destination stream.
    /// </summary>
    /// <param name="key">Logical stream key.</param>
    /// <param name="destination">Writable destination stream.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true" /> when payload exists; otherwise <see langword="false" />.</returns>
    ValueTask<bool> CopyToAsync(string key, Stream destination, CancellationToken ct = default);

    /// <summary>
    /// Removes a chunked payload manifest and all associated chunks.
    /// </summary>
    /// <param name="key">Logical stream key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true" /> when any stored part was removed.</returns>
    ValueTask<bool> RemoveAsync(string key, CancellationToken ct = default);
}

