using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;

namespace VapeCache.Extensions.AspNetCore;

/// <summary>
/// ASP.NET Core output-cache store backed by <see cref="ICacheService"/>.
/// </summary>
public sealed class VapeCacheOutputCacheStore(
    ICacheService cache,
    IOptionsMonitor<VapeCacheOutputCacheStoreOptions> optionsMonitor,
    ILogger<VapeCacheOutputCacheStore> logger) : IOutputCacheStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly byte[] EnvelopePrefix = "VCOUT1:"u8.ToArray();

    /// <inheritdoc />
    public async ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var normalizedKey = BuildCacheKey(key);
        var stored = await cache.GetAsync(normalizedKey, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return null;

        if (!HasEnvelopePrefix(stored))
            return stored;

        if (!TryDeserializeEnvelope(stored, out var envelope))
        {
            logger.LogWarning("Discarding malformed output-cache envelope for key {Key}.", normalizedKey);
            await cache.RemoveAsync(normalizedKey, cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (envelope.TagVersions.Count == 0)
            return envelope.Payload;

        if (await AreTagVersionsCurrentAsync(envelope.TagVersions, cancellationToken).ConfigureAwait(false))
            return envelope.Payload;

        await cache.RemoveAsync(normalizedKey, cancellationToken).ConfigureAwait(false);
        logger.LogDebug("Discarded stale output-cache entry for key {Key} after tag invalidation.", normalizedKey);
        return null;
    }

    /// <inheritdoc />
    public async ValueTask SetAsync(
        string key,
        byte[] value,
        string[]? tags,
        TimeSpan validFor,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        var options = optionsMonitor.CurrentValue;
        var normalizedKey = BuildCacheKey(key);
        var normalizedTags = NormalizeTags(tags);
        var ttl = validFor > TimeSpan.Zero ? validFor : options.DefaultTtl;

        var entryOptions = new CacheEntryOptions(
            Ttl: ttl,
            Intent: new CacheIntent(
                CacheIntentKind.ComputedView,
                Reason: "aspnetcore-output-cache",
                Owner: "aspnetcore-output-cache-store",
                Tags: normalizedTags));

        if (!options.EnableTagIndexing || normalizedTags.Length == 0)
        {
            await cache.SetAsync(normalizedKey, value, entryOptions, cancellationToken).ConfigureAwait(false);
            return;
        }

        var tagVersions = await CaptureTagVersionsAsync(normalizedTags, cancellationToken).ConfigureAwait(false);
        var wrappedPayload = SerializeEnvelope(new OutputCacheEnvelope
        {
            Payload = value,
            TagVersions = tagVersions
        });

        await cache.SetAsync(normalizedKey, wrappedPayload, entryOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        var options = optionsMonitor.CurrentValue;
        if (!options.EnableTagIndexing)
            return;

        var normalizedTag = tag.Trim();
        var currentVersion = await GetTagVersionAsync(normalizedTag, cancellationToken).ConfigureAwait(false);
        var nextVersion = currentVersion == long.MaxValue ? 1 : currentVersion + 1;

        await cache.SetAsync(
            BuildTagVersionKey(normalizedTag),
            SerializeTagVersion(nextVersion),
            new CacheEntryOptions(
                Intent: new CacheIntent(
                    CacheIntentKind.ComputedView,
                    Reason: "aspnetcore-output-cache-tag-version",
                    Owner: "aspnetcore-output-cache-store",
                    Tags: [normalizedTag])),
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation("Output-cache eviction by tag completed. Tag={Tag} Version={Version}", normalizedTag, nextVersion);
    }

    private async ValueTask<Dictionary<string, long>> CaptureTagVersionsAsync(string[] normalizedTags, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, long>(normalizedTags.Length, StringComparer.Ordinal);
        foreach (var tag in normalizedTags)
            result[tag] = await GetTagVersionAsync(tag, cancellationToken).ConfigureAwait(false);

        return result;
    }

    private async ValueTask<bool> AreTagVersionsCurrentAsync(
        IReadOnlyDictionary<string, long> expectedVersions,
        CancellationToken cancellationToken)
    {
        foreach (var entry in expectedVersions)
        {
            var current = await GetTagVersionAsync(entry.Key, cancellationToken).ConfigureAwait(false);
            if (current != entry.Value)
                return false;
        }

        return true;
    }

    private async ValueTask<long> GetTagVersionAsync(string tag, CancellationToken cancellationToken)
    {
        var payload = await cache.GetAsync(BuildTagVersionKey(tag), cancellationToken).ConfigureAwait(false);
        return TryDeserializeTagVersion(payload, out var version)
            ? version
            : 0;
    }

    private string BuildTagVersionKey(string tag) => $"{ResolveKeyPrefix()}:tag-version:{tag}";

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

    private static byte[] SerializeEnvelope(OutputCacheEnvelope envelope)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        var buffer = GC.AllocateUninitializedArray<byte>(EnvelopePrefix.Length + json.Length);
        EnvelopePrefix.AsSpan().CopyTo(buffer);
        json.AsSpan().CopyTo(buffer.AsSpan(EnvelopePrefix.Length));
        return buffer;
    }

    private static bool HasEnvelopePrefix(byte[] payload)
        => payload.AsSpan().StartsWith(EnvelopePrefix);

    private static bool TryDeserializeEnvelope(byte[] payload, out OutputCacheEnvelope envelope)
    {
        envelope = default!;
        if (!HasEnvelopePrefix(payload))
            return false;

        try
        {
            var parsed = JsonSerializer.Deserialize<OutputCacheEnvelope>(
                payload.AsSpan(EnvelopePrefix.Length),
                JsonOptions);
            if (parsed is null || parsed.Payload is null)
                return false;

            envelope = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private string BuildCacheKey(string key)
    {
        return $"{ResolveKeyPrefix()}:{key}";
    }

    private string ResolveKeyPrefix()
    {
        var options = optionsMonitor.CurrentValue;
        return string.IsNullOrWhiteSpace(options.KeyPrefix) ? "vapecache:output" : options.KeyPrefix.Trim();
    }

    private static string[] NormalizeTags(string[]? tags)
    {
        if (tags is null || tags.Length == 0)
            return Array.Empty<string>();

        var result = new List<string>(tags.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag))
                continue;

            var normalized = tag.Trim();
            if (seen.Add(normalized))
                result.Add(normalized);
        }

        return result.ToArray();
    }

    private sealed class OutputCacheEnvelope
    {
        public byte[] Payload { get; init; } = Array.Empty<byte>();
        public Dictionary<string, long> TagVersions { get; init; } = new(StringComparer.Ordinal);
    }
}
