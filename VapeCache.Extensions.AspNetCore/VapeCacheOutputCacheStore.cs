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
public sealed partial class VapeCacheOutputCacheStore(
    ICacheService cache,
    IOptionsMonitor<VapeCacheOutputCacheStoreOptions> optionsMonitor,
    ILogger<VapeCacheOutputCacheStore> logger) : IOutputCacheStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly byte[] EnvelopePrefix = "VCOUT1:"u8.ToArray();
    private static readonly CacheIntent OutputCacheIntentWithoutTags = new(
        CacheIntentKind.ComputedView,
        Reason: "aspnetcore-output-cache",
        Owner: "aspnetcore-output-cache-store",
        Tags: null);
    private string[]? _cachedSetIntentTags;
    private CacheIntent? _cachedSetIntent;

    /// <inheritdoc />
    public async ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var keyPrefix = ResolveKeyPrefix(optionsMonitor.CurrentValue);
        var normalizedKey = BuildCacheKey(key, keyPrefix);
        var stored = await cache.GetAsync(normalizedKey, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return null;

        if (!HasEnvelopePrefix(stored))
            return stored;

        if (!TryDeserializeEnvelope(stored, out var envelope))
        {
            LogDiscardingMalformedEnvelope(logger, normalizedKey);
            await cache.RemoveAsync(normalizedKey, cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (envelope.TagVersions.Count == 0)
            return envelope.Payload;

        if (await AreTagVersionsCurrentAsync(envelope.TagVersions, keyPrefix, cancellationToken).ConfigureAwait(false))
            return envelope.Payload;

        await cache.RemoveAsync(normalizedKey, cancellationToken).ConfigureAwait(false);
        LogDiscardedStaleEntry(logger, normalizedKey);
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
        var keyPrefix = ResolveKeyPrefix(options);
        var normalizedKey = BuildCacheKey(key, keyPrefix);
        var normalizedTags = NormalizeTags(tags);
        var ttl = validFor > TimeSpan.Zero ? validFor : options.DefaultTtl;
        var entryOptions = new CacheEntryOptions(
            Ttl: ttl,
            Intent: ResolveSetIntent(normalizedTags));

        if (!options.EnableTagIndexing || normalizedTags.Length == 0)
        {
            await cache.SetAsync(normalizedKey, value, entryOptions, cancellationToken).ConfigureAwait(false);
            return;
        }

        var tagVersions = await CaptureTagVersionsAsync(normalizedTags, keyPrefix, cancellationToken).ConfigureAwait(false);
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
        var keyPrefix = ResolveKeyPrefix(options);
        var currentVersion = await GetTagVersionAsync(normalizedTag, keyPrefix, cancellationToken).ConfigureAwait(false);
        var nextVersion = currentVersion == long.MaxValue ? 1 : currentVersion + 1;

        await cache.SetAsync(
            BuildTagVersionKey(normalizedTag, keyPrefix),
            SerializeTagVersion(nextVersion),
            new CacheEntryOptions(
                Intent: new CacheIntent(
                    CacheIntentKind.ComputedView,
                    Reason: "aspnetcore-output-cache-tag-version",
                    Owner: "aspnetcore-output-cache-store",
                    Tags: [normalizedTag])),
            cancellationToken).ConfigureAwait(false);

        LogEvictedOutputCacheTag(logger, normalizedTag, nextVersion);
    }

    private async ValueTask<Dictionary<string, long>> CaptureTagVersionsAsync(
        string[] normalizedTags,
        string keyPrefix,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, long>(normalizedTags.Length, StringComparer.Ordinal);
        foreach (var tag in normalizedTags)
            result[tag] = await GetTagVersionAsync(tag, keyPrefix, cancellationToken).ConfigureAwait(false);

        return result;
    }

    private async ValueTask<bool> AreTagVersionsCurrentAsync(
        IReadOnlyDictionary<string, long> expectedVersions,
        string keyPrefix,
        CancellationToken cancellationToken)
    {
        foreach (var entry in expectedVersions)
        {
            var current = await GetTagVersionAsync(entry.Key, keyPrefix, cancellationToken).ConfigureAwait(false);
            if (current != entry.Value)
                return false;
        }

        return true;
    }

    private async ValueTask<long> GetTagVersionAsync(string tag, string keyPrefix, CancellationToken cancellationToken)
    {
        var payload = await cache.GetAsync(BuildTagVersionKey(tag, keyPrefix), cancellationToken).ConfigureAwait(false);
        return TryDeserializeTagVersion(payload, out var version)
            ? version
            : 0;
    }

    private static string BuildTagVersionKey(string tag, string keyPrefix)
        => string.Concat(keyPrefix, ":tag-version:", tag);

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

    private static string BuildCacheKey(string key, string keyPrefix) => string.Concat(keyPrefix, ":", key);

    private static string ResolveKeyPrefix(VapeCacheOutputCacheStoreOptions options)
        => string.IsNullOrWhiteSpace(options.KeyPrefix) ? "vapecache:output" : options.KeyPrefix.Trim();

    private static string[] NormalizeTags(string[]? tags)
    {
        if (tags is null || tags.Length == 0)
            return Array.Empty<string>();

        if (CanReuseInputTags(tags))
            return tags;

        var result = new string[tags.Length];
        var count = 0;

        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag))
                continue;

            var normalized = tag.Trim();
            var duplicate = false;
            for (var i = 0; i < count; i++)
            {
                if (!string.Equals(result[i], normalized, StringComparison.Ordinal))
                    continue;

                duplicate = true;
                break;
            }

            if (!duplicate)
                result[count++] = normalized;
        }

        if (count == 0)
            return Array.Empty<string>();

        if (count == result.Length)
            return result;

        var trimmed = GC.AllocateUninitializedArray<string>(count);
        Array.Copy(result, trimmed, count);
        return trimmed;
    }

    private static bool CanReuseInputTags(string[] tags)
    {
        for (var i = 0; i < tags.Length; i++)
        {
            var tag = tags[i];
            if (string.IsNullOrWhiteSpace(tag))
                return false;

            if (char.IsWhiteSpace(tag[0]) || char.IsWhiteSpace(tag[^1]))
                return false;

            for (var j = 0; j < i; j++)
            {
                if (string.Equals(tags[j], tag, StringComparison.Ordinal))
                    return false;
            }
        }

        return true;
    }

    private CacheIntent ResolveSetIntent(string[] normalizedTags)
    {
        if (normalizedTags.Length == 0)
            return OutputCacheIntentWithoutTags;

        var cachedTags = Volatile.Read(ref _cachedSetIntentTags);
        if (ReferenceEquals(cachedTags, normalizedTags))
        {
            var cachedIntent = Volatile.Read(ref _cachedSetIntent);
            if (cachedIntent is not null)
                return cachedIntent;
        }

        var created = new CacheIntent(
            CacheIntentKind.ComputedView,
            Reason: "aspnetcore-output-cache",
            Owner: "aspnetcore-output-cache-store",
            Tags: normalizedTags);

        Volatile.Write(ref _cachedSetIntentTags, normalizedTags);
        Volatile.Write(ref _cachedSetIntent, created);
        return created;
    }

    private sealed class OutputCacheEnvelope
    {
        public byte[] Payload { get; init; } = Array.Empty<byte>();
        public Dictionary<string, long> TagVersions { get; init; } = new(StringComparer.Ordinal);
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning, Message = "Discarding malformed output-cache envelope for key {Key}.")]
    private static partial void LogDiscardingMalformedEnvelope(ILogger logger, string key);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Debug, Message = "Discarded stale output-cache entry for key {Key} after tag invalidation.")]
    private static partial void LogDiscardedStaleEntry(ILogger logger, string key);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Information, Message = "Output-cache eviction by tag completed. Tag={Tag} Version={Version}")]
    private static partial void LogEvictedOutputCacheTag(ILogger logger, string tag, long version);
}
