using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using VapeCache.Abstractions.Caching;

namespace VapeCache.Infrastructure.Caching;

internal sealed class ChunkedCacheStreamService(ICacheService cache) : ICacheChunkStreamService
{
    private const int MinChunkSizeBytes = 4 * 1024;
    private const int MaxChunkSizeBytes = 1024 * 1024;
    private const int MaxContentTypeBytes = 1024;
    private const int ManifestMagic = 0x31534356; // VCS1 (little-endian)
    private const int ManifestHeaderBytes = 4 + 4 + 4 + 8 + 2 + 2;

    /// <inheritdoc />
    public async ValueTask<CacheChunkStreamManifest> WriteAsync(
        string key,
        Stream source,
        CacheEntryOptions options = default,
        CacheChunkStreamWriteOptions? writeOptions = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
            throw new ArgumentException("Source stream must be readable.", nameof(source));

        var existing = await GetStoredManifestAsync(key, ct).ConfigureAwait(false);
        var resolvedWriteOptions = writeOptions ?? new CacheChunkStreamWriteOptions();
        var chunkSize = NormalizeChunkSize(resolvedWriteOptions.ChunkSizeBytes);
        var contentType = NormalizeContentType(resolvedWriteOptions.ContentType);
        var storageId = Guid.NewGuid().ToString("N");

        byte[]? rented = null;
        var writtenChunkCount = 0;
        long writtenBytes = 0;

        try
        {
            rented = ArrayPool<byte>.Shared.Rent(chunkSize);

            while (true)
            {
                var read = await source.ReadAsync(rented.AsMemory(0, chunkSize), ct).ConfigureAwait(false);
                if (read == 0)
                    break;

                var chunk = GC.AllocateUninitializedArray<byte>(read);
                Buffer.BlockCopy(rented, 0, chunk, 0, read);

                var chunkKey = GetChunkKey(key, storageId, writtenChunkCount);
                await cache.SetAsync(chunkKey, chunk, options, ct).ConfigureAwait(false);

                writtenChunkCount++;
                writtenBytes += read;
            }

            var stored = new StoredManifest(storageId, writtenChunkCount, chunkSize, writtenBytes, contentType);
            var manifestKey = GetManifestKey(key);
            var manifestBytes = SerializeManifest(in stored);
            await cache.SetAsync(manifestKey, manifestBytes, options, ct).ConfigureAwait(false);

            if (existing is not null && !string.Equals(existing.Value.StorageId, storageId, StringComparison.Ordinal))
                await RemoveChunksByManifestAsync(key, existing.Value, ct).ConfigureAwait(false);

            return stored.ToPublicManifest();
        }
        catch
        {
            await RemoveChunksByManifestAsync(
                key,
                new StoredManifest(storageId, writtenChunkCount, chunkSize, writtenBytes, contentType),
                ct).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <inheritdoc />
    public async ValueTask<CacheChunkStreamManifest?> GetManifestAsync(string key, CancellationToken ct = default)
    {
        var stored = await GetStoredManifestAsync(key, ct).ConfigureAwait(false);
        return stored?.ToPublicManifest();
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(
        string key,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var stored = await GetStoredManifestAsync(key, ct).ConfigureAwait(false);
        if (stored is null)
            yield break;

        for (var i = 0; i < stored.Value.ChunkCount; i++)
        {
            ct.ThrowIfCancellationRequested();

            var chunkKey = GetChunkKey(key, stored.Value.StorageId, i);
            var chunk = await cache.GetAsync(chunkKey, ct).ConfigureAwait(false);
            if (chunk is null)
                throw new InvalidDataException($"Chunk '{i}' is missing for stream key '{key}'.");

            yield return chunk;
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> CopyToAsync(string key, Stream destination, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("Destination stream must be writable.", nameof(destination));

        var stored = await GetStoredManifestAsync(key, ct).ConfigureAwait(false);
        if (stored is null)
            return false;

        for (var i = 0; i < stored.Value.ChunkCount; i++)
        {
            ct.ThrowIfCancellationRequested();

            var chunkKey = GetChunkKey(key, stored.Value.StorageId, i);
            var chunk = await cache.GetAsync(chunkKey, ct).ConfigureAwait(false);
            if (chunk is null)
                throw new InvalidDataException($"Chunk '{i}' is missing for stream key '{key}'.");

            await destination.WriteAsync(chunk, ct).ConfigureAwait(false);
        }

        return true;
    }

    /// <inheritdoc />
    public async ValueTask<bool> RemoveAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var stored = await GetStoredManifestAsync(key, ct).ConfigureAwait(false);
        var removedAny = false;

        if (stored is not null)
            removedAny = await RemoveChunksByManifestAsync(key, stored.Value, ct).ConfigureAwait(false);

        var manifestRemoved = await cache.RemoveAsync(GetManifestKey(key), ct).ConfigureAwait(false);
        return removedAny || manifestRemoved;
    }

    private async ValueTask<StoredManifest?> GetStoredManifestAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var manifestBytes = await cache.GetAsync(GetManifestKey(key), ct).ConfigureAwait(false);
        if (manifestBytes is null)
            return null;

        if (!TryDeserializeManifest(manifestBytes, out var stored))
            throw new InvalidDataException($"Stream manifest for key '{key}' is invalid.");

        return stored;
    }

    private async ValueTask<bool> RemoveChunksByManifestAsync(string key, StoredManifest manifest, CancellationToken ct)
    {
        var removedAny = false;
        for (var i = 0; i < manifest.ChunkCount; i++)
        {
            var chunkKey = GetChunkKey(key, manifest.StorageId, i);
            var removed = await cache.RemoveAsync(chunkKey, ct).ConfigureAwait(false);
            removedAny |= removed;
        }

        return removedAny;
    }

    private static int NormalizeChunkSize(int chunkSizeBytes)
    {
        if (chunkSizeBytes <= 0)
            return CacheChunkStreamWriteOptions.DefaultChunkSizeBytes;

        return Math.Clamp(chunkSizeBytes, MinChunkSizeBytes, MaxChunkSizeBytes);
    }

    private static string? NormalizeContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return null;

        var trimmed = contentType.Trim();
        if (trimmed.Length == 0)
            return null;

        return trimmed;
    }

    private static string GetManifestKey(string key) => $"{key}:stream:manifest";

    private static string GetChunkKey(string key, string storageId, int chunkIndex)
        => $"{key}:stream:{storageId}:chunk:{chunkIndex:D8}";

    private static byte[] SerializeManifest(in StoredManifest stored)
    {
        var contentTypeBytes = string.IsNullOrEmpty(stored.ContentType)
            ? Array.Empty<byte>()
            : Encoding.UTF8.GetBytes(stored.ContentType);

        if (contentTypeBytes.Length > MaxContentTypeBytes)
            throw new InvalidOperationException($"ContentType metadata exceeds max of {MaxContentTypeBytes} bytes.");

        var storageIdBytes = Encoding.ASCII.GetBytes(stored.StorageId);
        if (storageIdBytes.Length == 0 || storageIdBytes.Length > ushort.MaxValue)
            throw new InvalidOperationException("Storage identifier length is invalid.");

        var payload = GC.AllocateUninitializedArray<byte>(ManifestHeaderBytes + storageIdBytes.Length + contentTypeBytes.Length);
        var span = payload.AsSpan();
        var cursor = 0;

        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(cursor, 4), ManifestMagic);
        cursor += 4;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(cursor, 4), stored.ChunkCount);
        cursor += 4;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(cursor, 4), stored.ChunkSizeBytes);
        cursor += 4;
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(cursor, 8), stored.ContentLengthBytes);
        cursor += 8;
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(cursor, 2), (ushort)storageIdBytes.Length);
        cursor += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(cursor, 2), (ushort)contentTypeBytes.Length);
        cursor += 2;

        storageIdBytes.CopyTo(span.Slice(cursor, storageIdBytes.Length));
        cursor += storageIdBytes.Length;

        if (contentTypeBytes.Length > 0)
            contentTypeBytes.CopyTo(span.Slice(cursor, contentTypeBytes.Length));

        return payload;
    }

    private static bool TryDeserializeManifest(ReadOnlySpan<byte> payload, out StoredManifest manifest)
    {
        manifest = default;
        if (payload.Length < ManifestHeaderBytes)
            return false;

        var cursor = 0;
        var magic = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(cursor, 4));
        cursor += 4;
        if (magic != ManifestMagic)
            return false;

        var chunkCount = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(cursor, 4));
        cursor += 4;
        var chunkSizeBytes = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(cursor, 4));
        cursor += 4;
        var contentLengthBytes = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(cursor, 8));
        cursor += 8;
        var storageIdLength = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
        cursor += 2;
        var contentTypeLength = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
        cursor += 2;

        if (chunkCount < 0 || chunkSizeBytes <= 0 || contentLengthBytes < 0)
            return false;
        if (storageIdLength == 0 || contentTypeLength > MaxContentTypeBytes)
            return false;

        var expectedLength = ManifestHeaderBytes + storageIdLength + contentTypeLength;
        if (payload.Length != expectedLength)
            return false;

        var storageId = Encoding.ASCII.GetString(payload.Slice(cursor, storageIdLength));
        cursor += storageIdLength;
        var contentType = contentTypeLength == 0
            ? null
            : Encoding.UTF8.GetString(payload.Slice(cursor, contentTypeLength));

        manifest = new StoredManifest(storageId, chunkCount, chunkSizeBytes, contentLengthBytes, contentType);
        return true;
    }

    private readonly record struct StoredManifest(
        string StorageId,
        int ChunkCount,
        int ChunkSizeBytes,
        long ContentLengthBytes,
        string? ContentType)
    {
        public CacheChunkStreamManifest ToPublicManifest()
            => new(ChunkCount, ChunkSizeBytes, ContentLengthBytes, ContentType);
    }
}
