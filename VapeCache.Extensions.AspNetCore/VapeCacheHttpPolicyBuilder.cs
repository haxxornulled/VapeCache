namespace VapeCache.Extensions.AspNetCore;

/// <summary>
/// Fluent builder for endpoint-level output cache behavior.
/// </summary>
public sealed class VapeCacheHttpPolicyBuilder
{
    private readonly VapeCacheHttpPolicyOptions _options;

    internal VapeCacheHttpPolicyBuilder(VapeCacheHttpPolicyOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Sets absolute cache duration.
    /// </summary>
    public VapeCacheHttpPolicyBuilder Ttl(TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl), ttl, "TTL must be greater than zero.");

        _options.Ttl = ttl;
        return this;
    }

    /// <summary>
    /// Disables storage for this policy.
    /// </summary>
    public VapeCacheHttpPolicyBuilder NoStore()
    {
        _options.NoStore = true;
        return this;
    }

    /// <summary>
    /// Varies cache keys by query parameters.
    /// </summary>
    public VapeCacheHttpPolicyBuilder VaryByQuery(params string[] queryKeys)
    {
        _options.VaryByQuery = true;
        _options.VaryByQueryKeys = VapeCacheHttpPolicyApplier.Normalize(queryKeys);
        return this;
    }

    /// <summary>
    /// Varies cache keys by header names.
    /// </summary>
    public VapeCacheHttpPolicyBuilder VaryByHeaders(params string[] headerNames)
    {
        _options.VaryByHeaderNames = VapeCacheHttpPolicyApplier.Normalize(headerNames);
        return this;
    }

    /// <summary>
    /// Varies cache keys by route values.
    /// </summary>
    public VapeCacheHttpPolicyBuilder VaryByRouteValues(params string[] routeValueNames)
    {
        _options.VaryByRouteValueNames = VapeCacheHttpPolicyApplier.Normalize(routeValueNames);
        return this;
    }

    /// <summary>
    /// Adds cache tags for invalidation.
    /// </summary>
    public VapeCacheHttpPolicyBuilder Tags(params string[] tags)
    {
        _options.Tags = VapeCacheHttpPolicyApplier.Normalize(tags);
        return this;
    }

    /// <summary>
    /// Sets an optional cache key prefix.
    /// </summary>
    public VapeCacheHttpPolicyBuilder CacheKeyPrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            throw new ArgumentException("Cache key prefix is required.", nameof(prefix));

        _options.CacheKeyPrefix = prefix.Trim();
        return this;
    }

    /// <summary>
    /// Enables or disables request locking.
    /// </summary>
    public VapeCacheHttpPolicyBuilder Locking(bool enabled = true)
    {
        _options.Locking = enabled;
        return this;
    }

    /// <summary>
    /// Adds optional intent metadata.
    /// </summary>
    public VapeCacheHttpPolicyBuilder WithIntent(string kind, string? reason = null)
    {
        if (string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("Intent kind is required.", nameof(kind));

        _options.IntentKind = kind.Trim();
        _options.IntentReason = reason?.Trim();
        return this;
    }
}

/// <summary>
/// Registry used to define named ASP.NET Core cache policies with VapeCache-friendly ergonomics.
/// </summary>
public sealed class VapeCacheAspNetPolicyRegistry
{
    private readonly Dictionary<string, VapeCacheHttpPolicyOptions> _policies = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Adds a named policy.
    /// </summary>
    public VapeCacheAspNetPolicyRegistry AddPolicy(string policyName, Action<VapeCacheHttpPolicyBuilder> configure)
    {
        if (string.IsNullOrWhiteSpace(policyName))
            throw new ArgumentException("Policy name is required.", nameof(policyName));
        ArgumentNullException.ThrowIfNull(configure);

        var normalizedName = policyName.Trim();
        if (_policies.ContainsKey(normalizedName))
            throw new InvalidOperationException($"Policy '{normalizedName}' is already registered.");

        var options = new VapeCacheHttpPolicyOptions();
        configure(new VapeCacheHttpPolicyBuilder(options));
        _policies[normalizedName] = options;
        return this;
    }

    internal IReadOnlyDictionary<string, VapeCacheHttpPolicyOptions> Snapshot()
    {
        return _policies.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }
}
