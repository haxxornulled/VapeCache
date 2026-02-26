namespace VapeCache.Abstractions.Caching;

/// <summary>
/// Controls how payload streams are chunked and stored in cache.
/// </summary>
public sealed class CacheChunkStreamWriteOptions
{
    /// <summary>
    /// Default chunk size for large payload streaming.
    /// </summary>
    public const int DefaultChunkSizeBytes = 64 * 1024;

    /// <summary>
    /// Chunk size in bytes used when persisting stream content.
    /// </summary>
    public int ChunkSizeBytes { get; set; } = DefaultChunkSizeBytes;

    /// <summary>
    /// Optional content type metadata (for example, <c>video/mp4</c>).
    /// </summary>
    public string? ContentType { get; set; }
}

