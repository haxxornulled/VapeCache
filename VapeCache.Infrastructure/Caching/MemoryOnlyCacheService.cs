using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using VapeCache.Abstractions.Caching;
using VapeCache.Core.Policies;

namespace VapeCache.Infrastructure.Caching;

/// <summary>
/// In-memory-first cache service that preserves tag and zone invalidation semantics
/// without requiring Redis or hybrid breaker behavior.
/// </summary>
internal sealed class MemoryOnlyCacheService : ICacheService, ICacheTagService
{
    private const string TagVersionKeyPrefix = "vapecache:tag:v1:";
    private static readonly byte[] BinaryTagEnvelopePrefix = "VCTAG2:"u8.ToArray();
    private static readonly CacheIntent TagVersionWriteIntent = new(
        CacheIntentKind.ComputedView,
        Reason: "cache-tag-version",
        Owner: "memory");

    private readonly InMemoryCacheService _memory;

    public MemoryOnlyCacheService(InMemoryCacheService memory)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
    }

    public string Name => _memory.Name;

    public async ValueTask<byte[]?> GetAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var payload = await _memory.GetAsync(key, ct).ConfigureAwait(false);
        if (payload is null || IsMetadataKey(key))
            return payload;

        return await ResolveTaggedPayloadAsync(key, payload, ct).ConfigureAwait(false);
    }

    public async ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CacheEntryOptions options, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (IsMetadataKey(key))
        {
            await _memory.SetAsync(key, value, options, ct).ConfigureAwait(false);
            return;
        }

        var valueToStore = await WrapPayloadWithTagVersionsAsync(value, options, ct).ConfigureAwait(false);
        await _memory.SetAsync(key, valueToStore, options, ct).ConfigureAwait(false);
    }

    public ValueTask<bool> RemoveAsync(string key, CancellationToken ct)
        => _memory.RemoveAsync(key, ct);

    public async ValueTask<T?> GetAsync<T>(string key, SpanDeserializer<T> deserialize, CancellationToken ct)
    {
        var bytes = await GetAsync(key, ct).ConfigureAwait(false);
        return bytes is null ? default : deserialize(bytes);
    }

    public async ValueTask SetAsync<T>(string key, T value, Action<IBufferWriter<byte>, T> serialize, CacheEntryOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var buffer = new PooledByteBufferWriter(256);
        serialize(buffer, value);
        await SetAsync(key, buffer.WrittenMemory, options, ct).ConfigureAwait(false);
    }

    public async ValueTask<T> GetOrSetAsync<T>(
        string key,
        Func<CancellationToken, ValueTask<T>> factory,
        Action<IBufferWriter<byte>, T> serialize,
        SpanDeserializer<T> deserialize,
        CacheEntryOptions options,
        CancellationToken ct)
    {
        var cached = await GetAsync(key, deserialize, ct).ConfigureAwait(false);
        if (cached is not null)
            return cached;

        var created = await factory(ct).ConfigureAwait(false);
        await SetAsync(key, created, serialize, options, ct).ConfigureAwait(false);
        return created;
    }

    public async ValueTask<long> InvalidateTagAsync(string tag, CancellationToken ct = default)
    {
        var normalizedTag = CacheTagPolicy.NormalizeTag(tag);
        var currentVersion = await GetCurrentTagVersionAsync(normalizedTag, ct).ConfigureAwait(false);
        var nextVersion = currentVersion == long.MaxValue ? 1 : currentVersion + 1;
        await WriteTagVersionAsync(normalizedTag, nextVersion, ct).ConfigureAwait(false);
        return nextVersion;
    }

    public ValueTask<long> GetTagVersionAsync(string tag, CancellationToken ct = default)
        => GetCurrentTagVersionAsync(CacheTagPolicy.NormalizeTag(tag), ct);

    public ValueTask<long> InvalidateZoneAsync(string zone, CancellationToken ct = default)
        => InvalidateTagAsync(CacheTagConventions.ToZoneTag(zone), ct);

    public ValueTask<long> GetZoneVersionAsync(string zone, CancellationToken ct = default)
        => GetTagVersionAsync(CacheTagConventions.ToZoneTag(zone), ct);

    private async ValueTask<ReadOnlyMemory<byte>> WrapPayloadWithTagVersionsAsync(
        ReadOnlyMemory<byte> payload,
        CacheEntryOptions options,
        CancellationToken ct)
    {
        var tags = CacheTagPolicy.NormalizeTags(existingTags: null, additionalTags: options.Intent?.Tags);
        if (tags.Length == 0)
            return payload;

        var tagVersions = await CaptureTagVersionsAsync(tags, ct).ConfigureAwait(false);
        return SerializeTagEnvelope(payload, tagVersions);
    }

    private async ValueTask<TagVersionSnapshot[]> CaptureTagVersionsAsync(string[] normalizedTags, CancellationToken ct)
    {
        var result = new TagVersionSnapshot[normalizedTags.Length];
        for (var i = 0; i < normalizedTags.Length; i++)
        {
            var tag = normalizedTags[i];
            result[i] = new TagVersionSnapshot(tag, await GetCurrentTagVersionAsync(tag, ct).ConfigureAwait(false));
        }

        return result;
    }

    private async ValueTask<byte[]?> ResolveTaggedPayloadAsync(string key, byte[]? payload, CancellationToken ct)
    {
        if (payload is null || !TryDeserializeBinaryTagEnvelope(payload, out var envelope))
            return payload;

        if (envelope.TagVersions.Length == 0
            || await AreTagVersionsCurrentAsync(envelope.TagVersions, ct).ConfigureAwait(false))
        {
            return envelope.ToArray();
        }

        await _memory.RemoveAsync(key, ct).ConfigureAwait(false);
        return null;
    }

    private async ValueTask<bool> AreTagVersionsCurrentAsync(TagVersionSnapshot[] expectedVersions, CancellationToken ct)
    {
        for (var i = 0; i < expectedVersions.Length; i++)
        {
            var expected = expectedVersions[i];
            var currentVersion = await GetCurrentTagVersionAsync(expected.Tag, ct).ConfigureAwait(false);
            if (currentVersion != expected.Version)
                return false;
        }

        return true;
    }

    private async ValueTask<long> GetCurrentTagVersionAsync(string normalizedTag, CancellationToken ct)
    {
        var payload = await _memory.GetAsync(BuildTagVersionKey(normalizedTag), ct).ConfigureAwait(false);
        return TryDeserializeTagVersion(payload, out var version) ? version : 0;
    }

    private ValueTask WriteTagVersionAsync(string normalizedTag, long version, CancellationToken ct)
    {
        var payload = SerializeTagVersion(version);
        var writeOptions = new CacheEntryOptions(Intent: TagVersionWriteIntent);
        return _memory.SetAsync(BuildTagVersionKey(normalizedTag), payload, writeOptions, ct);
    }

    private static bool IsMetadataKey(string key)
        => key.StartsWith(TagVersionKeyPrefix, StringComparison.Ordinal)
           || key.Contains(":tag-version:", StringComparison.Ordinal);

    private static string BuildTagVersionKey(string normalizedTag)
        => $"{TagVersionKeyPrefix}{normalizedTag}";

    private static byte[] SerializeTagVersion(long version)
    {
        var bytes = GC.AllocateUninitializedArray<byte>(sizeof(long));
        BinaryPrimitives.WriteInt64LittleEndian(bytes, version);
        return bytes;
    }

    private static bool TryDeserializeTagVersion(byte[]? payload, out long version)
    {
        version = 0;
        if (payload is null || payload.Length != sizeof(long))
            return false;

        version = BinaryPrimitives.ReadInt64LittleEndian(payload);
        return true;
    }

    private static byte[] SerializeTagEnvelope(ReadOnlyMemory<byte> payload, TagVersionSnapshot[] tagVersions)
    {
        var length = BinaryTagEnvelopePrefix.Length + sizeof(int) + sizeof(int) + payload.Length;
        for (var i = 0; i < tagVersions.Length; i++)
        {
            var tag = tagVersions[i];
            length += sizeof(int) + Encoding.UTF8.GetByteCount(tag.Tag) + sizeof(long);
        }

        var buffer = GC.AllocateUninitializedArray<byte>(length);
        var span = buffer.AsSpan();
        var offset = 0;

        BinaryTagEnvelopePrefix.CopyTo(buffer, offset);
        offset += BinaryTagEnvelopePrefix.Length;

        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, sizeof(int)), tagVersions.Length);
        offset += sizeof(int);

        for (var i = 0; i < tagVersions.Length; i++)
        {
            var tag = tagVersions[i];
            var tagLength = Encoding.UTF8.GetByteCount(tag.Tag);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, sizeof(int)), tagLength);
            offset += sizeof(int);
            offset += Encoding.UTF8.GetBytes(tag.Tag, span.Slice(offset, tagLength));
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset, sizeof(long)), tag.Version);
            offset += sizeof(long);
        }

        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, sizeof(int)), payload.Length);
        offset += sizeof(int);
        payload.Span.CopyTo(span.Slice(offset, payload.Length));
        return buffer;
    }

    private static bool TryDeserializeBinaryTagEnvelope(byte[] payload, out BinaryTaggedCacheEnvelope envelope)
    {
        envelope = default;
        var span = payload.AsSpan();
        if (!span.StartsWith(BinaryTagEnvelopePrefix))
            return false;

        var offset = BinaryTagEnvelopePrefix.Length;
        if (!TryReadInt32(span, ref offset, out var tagCount)
            || tagCount < 0
            || tagCount > 1024)
        {
            return false;
        }

        var tagVersions = tagCount == 0 ? Array.Empty<TagVersionSnapshot>() : new TagVersionSnapshot[tagCount];
        for (var i = 0; i < tagCount; i++)
        {
            if (!TryReadInt32(span, ref offset, out var tagByteCount)
                || tagByteCount < 0
                || span.Length - offset < tagByteCount + sizeof(long))
            {
                return false;
            }

            var tag = Encoding.UTF8.GetString(span.Slice(offset, tagByteCount));
            offset += tagByteCount;
            if (!TryReadInt64(span, ref offset, out var version))
                return false;

            tagVersions[i] = new TagVersionSnapshot(tag, version);
        }

        if (!TryReadInt32(span, ref offset, out var payloadLength)
            || payloadLength < 0
            || span.Length - offset != payloadLength)
        {
            return false;
        }

        envelope = new BinaryTaggedCacheEnvelope(payload, offset, payloadLength, tagVersions);
        return true;
    }

    private static bool TryReadInt32(ReadOnlySpan<byte> buffer, ref int offset, out int value)
    {
        value = 0;
        if (buffer.Length - offset < sizeof(int))
            return false;

        value = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset, sizeof(int)));
        offset += sizeof(int);
        return true;
    }

    private static bool TryReadInt64(ReadOnlySpan<byte> buffer, ref int offset, out long value)
    {
        value = 0;
        if (buffer.Length - offset < sizeof(long))
            return false;

        value = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset, sizeof(long)));
        offset += sizeof(long);
        return true;
    }

    private readonly struct TagVersionSnapshot
    {
        public TagVersionSnapshot(string tag, long version)
        {
            Tag = tag;
            Version = version;
        }

        public string Tag { get; }
        public long Version { get; }
    }

    private readonly struct BinaryTaggedCacheEnvelope
    {
        public BinaryTaggedCacheEnvelope(
            byte[] buffer,
            int payloadOffset,
            int payloadLength,
            TagVersionSnapshot[] tagVersions)
        {
            Buffer = buffer;
            PayloadOffset = payloadOffset;
            PayloadLength = payloadLength;
            TagVersions = tagVersions;
        }

        public byte[] Buffer { get; }
        public int PayloadOffset { get; }
        public int PayloadLength { get; }
        public TagVersionSnapshot[] TagVersions { get; }

        public byte[] ToArray()
        {
            var result = GC.AllocateUninitializedArray<byte>(PayloadLength);
            Buffer.AsSpan(PayloadOffset, PayloadLength).CopyTo(result);
            return result;
        }
    }
}
