namespace VapeCache.Abstractions.Caching;

/// <summary>
/// Describes a chunked stream payload stored in cache.
/// </summary>
/// <param name="ChunkCount">Total number of chunks.</param>
/// <param name="ChunkSizeBytes">Configured chunk size used at write time.</param>
/// <param name="ContentLengthBytes">Total content length in bytes.</param>
/// <param name="ContentType">Optional content type (for example, <c>video/mp4</c>).</param>
public readonly record struct CacheChunkStreamManifest(
    int ChunkCount,
    int ChunkSizeBytes,
    long ContentLengthBytes,
    string? ContentType);

