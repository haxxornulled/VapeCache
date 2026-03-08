using Microsoft.Extensions.Options;
using VapeCache.Application.Caching.Invalidation.Events;
using VapeCache.Features.Invalidation;

namespace VapeCache.Application.Caching.Invalidation.Policies;

/// <summary>
/// Maps entity-change events into invalidation targets based on the active runtime profile.
/// </summary>
public sealed class ProfileAwareEntityCacheChangedInvalidationPolicy(
    IOptionsMonitor<CacheInvalidationOptions> optionsMonitor)
    : ICacheInvalidationPolicy<EntityCacheChangedEvent>
{
    private static readonly string[] Empty = [];

    private readonly IOptionsMonitor<CacheInvalidationOptions> _optionsMonitor = optionsMonitor;

    /// <summary>
    /// Executes build plan async.
    /// </summary>
    public ValueTask<CacheInvalidationPlan> BuildPlanAsync(
        EntityCacheChangedEvent eventData,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var profile = _optionsMonitor.CurrentValue.Profile;
        var tags = BuildTags(eventData);
        var zones = Normalize(eventData.Zones);

        return profile switch
        {
            CacheInvalidationProfile.HighTrafficSite => ValueTask.FromResult(
                new CacheInvalidationPlan(tags, zones, BuildKeys(eventData, includeDerived: true))),

            CacheInvalidationProfile.DesktopApp => ValueTask.FromResult(
                new CacheInvalidationPlan(keys: BuildKeys(eventData, includeDerived: true))),

            _ => ValueTask.FromResult(new CacheInvalidationPlan(tags, zones, keys: null))
        };
    }

    private static string[] BuildTags(EntityCacheChangedEvent eventData)
    {
        var tags = Normalize(eventData.Tags);
        var ids = Normalize(eventData.EntityIds);
        if (ids.Length == 0 || string.IsNullOrWhiteSpace(eventData.EntityName))
            return tags;

        var entityName = eventData.EntityName.Trim();
        HashSet<string>? set = null;
        if (tags.Length > 0)
            set = new HashSet<string>(tags, StringComparer.Ordinal);

        for (var i = 0; i < ids.Length; i++)
        {
            set ??= new HashSet<string>(StringComparer.Ordinal);
            set.Add($"{entityName}:{ids[i]}");
        }

        if (set is null || set.Count == 0)
            return Empty;

        var result = new string[set.Count];
        set.CopyTo(result);
        return result;
    }

    private static string[] BuildKeys(EntityCacheChangedEvent eventData, bool includeDerived)
    {
        var keys = Normalize(eventData.Keys);
        if (!includeDerived)
            return keys;

        var ids = Normalize(eventData.EntityIds);
        if (ids.Length == 0 || string.IsNullOrWhiteSpace(eventData.EntityName))
            return keys;

        var prefixes = eventData.KeyPrefixes is null
            ? [eventData.EntityName.Trim()]
            : Normalize(eventData.KeyPrefixes);

        if (prefixes.Length == 0)
            return keys;

        HashSet<string>? set = null;
        if (keys.Length > 0)
            set = new HashSet<string>(keys, StringComparer.Ordinal);

        for (var p = 0; p < prefixes.Length; p++)
        {
            for (var i = 0; i < ids.Length; i++)
            {
                set ??= new HashSet<string>(StringComparer.Ordinal);
                set.Add($"{prefixes[p]}:{ids[i]}");
            }
        }

        if (set is null || set.Count == 0)
            return Empty;

        var result = new string[set.Count];
        set.CopyTo(result);
        return result;
    }

    private static string[] Normalize(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
            return Empty;

        HashSet<string>? normalized = null;
        for (var i = 0; i < values.Count; i++)
        {
            var value = values[i];
            if (string.IsNullOrWhiteSpace(value))
                continue;

            normalized ??= new HashSet<string>(StringComparer.Ordinal);
            normalized.Add(value.Trim());
        }

        if (normalized is null || normalized.Count == 0)
            return Empty;

        var result = new string[normalized.Count];
        normalized.CopyTo(result);
        return result;
    }
}
