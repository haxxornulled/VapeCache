using Microsoft.AspNetCore.OutputCaching;

namespace VapeCache.Extensions.AspNetCore;

/// <summary>
/// HTTP-facing policy settings for applying VapeCache-oriented output-cache behavior.
/// </summary>
public sealed class VapeCacheHttpPolicyOptions
{
    /// <summary>
    /// Gets or sets the absolute cache duration.
    /// </summary>
    public TimeSpan? Ttl { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether caching should be disabled for the policy.
    /// </summary>
    public bool NoStore { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether query values should be included in the cache key.
    /// When true and <see cref="VaryByQueryKeys"/> is empty, all query keys are considered.
    /// </summary>
    public bool VaryByQuery { get; set; }

    /// <summary>
    /// Gets or sets the query keys used for vary behavior.
    /// </summary>
    public string[] VaryByQueryKeys { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the header names used for vary behavior.
    /// </summary>
    public string[] VaryByHeaderNames { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the route value names used for vary behavior.
    /// </summary>
    public string[] VaryByRouteValueNames { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets cache tags used for invalidation.
    /// </summary>
    public string[] Tags { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets an optional cache key prefix.
    /// </summary>
    public string? CacheKeyPrefix { get; set; }

    /// <summary>
    /// Gets or sets an optional explicit lock behavior override.
    /// </summary>
    public bool? Locking { get; set; }

    /// <summary>
    /// Gets or sets optional intent classification metadata for diagnostics/documentation.
    /// </summary>
    public string? IntentKind { get; set; }

    /// <summary>
    /// Gets or sets optional intent reason metadata for diagnostics/documentation.
    /// </summary>
    public string? IntentReason { get; set; }
}

internal static class VapeCacheHttpPolicyApplier
{
    public static void Apply(VapeCacheHttpPolicyOptions options, OutputCachePolicyBuilder policyBuilder)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(policyBuilder);

        if (options.NoStore)
        {
            policyBuilder.NoCache();
            return;
        }

        if (options.Ttl.HasValue)
            policyBuilder.Expire(options.Ttl.Value);

        var queryKeys = Normalize(options.VaryByQueryKeys);
        if (options.VaryByQuery || queryKeys.Length > 0)
            policyBuilder.SetVaryByQuery(queryKeys.Length == 0 ? ["*"] : queryKeys);

        var headerNames = Normalize(options.VaryByHeaderNames);
        if (headerNames.Length > 0)
            policyBuilder.SetVaryByHeader(headerNames);

        var routeValueNames = Normalize(options.VaryByRouteValueNames);
        if (routeValueNames.Length > 0)
            policyBuilder.SetVaryByRouteValue(routeValueNames);

        var tags = Normalize(options.Tags);
        if (tags.Length > 0)
            policyBuilder.Tag(tags);

        if (!string.IsNullOrWhiteSpace(options.CacheKeyPrefix))
            policyBuilder.SetCacheKeyPrefix(options.CacheKeyPrefix.Trim());

        if (options.Locking.HasValue)
            policyBuilder.SetLocking(options.Locking.Value);
    }

    public static string[] Normalize(IEnumerable<string>? values)
    {
        if (values is null)
            return Array.Empty<string>();

        List<string>? normalized = null;
        HashSet<string>? dedupe = null;

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var trimmed = value.Trim();
            if (trimmed.Length == 0)
                continue;

            dedupe ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!dedupe.Add(trimmed))
                continue;

            normalized ??= new List<string>(capacity: 4);
            normalized.Add(trimmed);
        }

        return normalized is null ? Array.Empty<string>() : normalized.ToArray();
    }
}
