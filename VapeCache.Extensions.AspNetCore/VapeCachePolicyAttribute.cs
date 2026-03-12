using Microsoft.AspNetCore.OutputCaching;

namespace VapeCache.Extensions.AspNetCore;

/// <summary>
/// Ergonomic cache-policy attribute that maps to ASP.NET Core <see cref="OutputCacheAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class VapeCachePolicyAttribute : Attribute
{
    /// <summary>
    /// Initializes a new attribute instance.
    /// </summary>
    public VapeCachePolicyAttribute()
    {
    }

    /// <summary>
    /// Initializes a new attribute instance for a named policy.
    /// </summary>
    public VapeCachePolicyAttribute(string policyName)
    {
        if (string.IsNullOrWhiteSpace(policyName))
            throw new ArgumentException("Policy name is required.", nameof(policyName));

        PolicyName = policyName.Trim();
    }

    /// <summary>
    /// Gets or sets named policy reference.
    /// </summary>
    public string? PolicyName { get; set; }

    /// <summary>
    /// Gets or sets TTL in whole seconds.
    /// </summary>
    public int TtlSeconds { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether storage should be disabled.
    /// </summary>
    public bool NoStore { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether all query parameters should be used for vary-by-key behavior.
    /// </summary>
    public bool VaryByQuery { get; set; }

    /// <summary>
    /// Gets or sets explicit query keys used for vary behavior.
    /// </summary>
    public string[] VaryByQueryKeys { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets header names used for vary behavior.
    /// </summary>
    public string[] VaryByHeaders { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets route value names used for vary behavior.
    /// </summary>
    public string[] VaryByRouteValues { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets cache tags.
    /// </summary>
    public string[] CacheTags { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Optional intent metadata for diagnostics/documentation.
    /// </summary>
    public string? IntentKind { get; set; }

    /// <summary>
    /// Optional intent reason metadata for diagnostics/documentation.
    /// </summary>
    public string? IntentReason { get; set; }

    /// <summary>
    /// Creates an ASP.NET Core output-cache attribute from this policy metadata.
    /// </summary>
    public OutputCacheAttribute ToOutputCacheAttribute()
    {
        var queryKeys = VapeCacheHttpPolicyApplier.Normalize(VaryByQueryKeys);
        var normalizedQueryKeys = (VaryByQuery, queryKeys.Length) switch
        {
            (_, > 0) => queryKeys,
            (true, 0) => ["*"],
            _ => Array.Empty<string>()
        };

        if (TtlSeconds < 0)
            throw new InvalidOperationException("TtlSeconds cannot be negative.");

        return new OutputCacheAttribute
        {
            PolicyName = string.IsNullOrWhiteSpace(PolicyName) ? null : PolicyName.Trim(),
            NoStore = NoStore,
            Duration = TtlSeconds,
            VaryByQueryKeys = normalizedQueryKeys,
            VaryByHeaderNames = VapeCacheHttpPolicyApplier.Normalize(VaryByHeaders),
            VaryByRouteValueNames = VapeCacheHttpPolicyApplier.Normalize(VaryByRouteValues),
            Tags = VapeCacheHttpPolicyApplier.Normalize(CacheTags)
        };
    }
}
