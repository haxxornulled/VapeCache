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
    private readonly Lock _indexGate = new();
    private readonly Dictionary<string, HashSet<string>> _keysByTag = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _tagsByKey = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
        => cache.GetAsync(BuildCacheKey(key), cancellationToken);

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

        await cache.SetAsync(normalizedKey, value, entryOptions, cancellationToken).ConfigureAwait(false);

        if (options.EnableTagIndexing)
            UpdateTagIndexes(normalizedKey, normalizedTags);
    }

    /// <inheritdoc />
    public async ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        var options = optionsMonitor.CurrentValue;
        if (!options.EnableTagIndexing)
            return;

        var normalizedTag = tag.Trim();
        string[] keys;
        lock (_indexGate)
        {
            if (!_keysByTag.TryGetValue(normalizedTag, out var keySet) || keySet.Count == 0)
                return;
            keys = keySet.ToArray();
        }

        foreach (var cacheKey in keys)
        {
            await cache.RemoveAsync(cacheKey, cancellationToken).ConfigureAwait(false);
            RemoveKeyFromIndexes(cacheKey);
        }

        logger.LogInformation("Output-cache eviction by tag completed. Tag={Tag} EvictedKeys={Count}", normalizedTag, keys.Length);
    }

    private void UpdateTagIndexes(string cacheKey, string[] tags)
    {
        lock (_indexGate)
        {
            if (_tagsByKey.TryGetValue(cacheKey, out var existingTags))
            {
                foreach (var existingTag in existingTags)
                {
                    if (_keysByTag.TryGetValue(existingTag, out var keysForTag))
                    {
                        keysForTag.Remove(cacheKey);
                        if (keysForTag.Count == 0)
                            _keysByTag.Remove(existingTag);
                    }
                }
            }

            var newTagSet = new HashSet<string>(tags, StringComparer.Ordinal);
            _tagsByKey[cacheKey] = newTagSet;

            foreach (var tag in newTagSet)
            {
                if (!_keysByTag.TryGetValue(tag, out var keysForTag))
                {
                    keysForTag = new HashSet<string>(StringComparer.Ordinal);
                    _keysByTag[tag] = keysForTag;
                }

                keysForTag.Add(cacheKey);
            }
        }
    }

    private void RemoveKeyFromIndexes(string cacheKey)
    {
        lock (_indexGate)
        {
            if (!_tagsByKey.TryGetValue(cacheKey, out var tags))
                return;

            foreach (var tag in tags)
            {
                if (_keysByTag.TryGetValue(tag, out var keysForTag))
                {
                    keysForTag.Remove(cacheKey);
                    if (keysForTag.Count == 0)
                        _keysByTag.Remove(tag);
                }
            }

            _tagsByKey.Remove(cacheKey);
        }
    }

    private string BuildCacheKey(string key)
    {
        var options = optionsMonitor.CurrentValue;
        var prefix = string.IsNullOrWhiteSpace(options.KeyPrefix) ? "vapecache:output" : options.KeyPrefix.Trim();
        return $"{prefix}:{key}";
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
}
