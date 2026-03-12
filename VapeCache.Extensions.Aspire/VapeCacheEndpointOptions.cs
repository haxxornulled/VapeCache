namespace VapeCache.Extensions.Aspire;

/// <summary>
/// Options for automatic mapping of VapeCache operational endpoints.
/// </summary>
public sealed class VapeCacheEndpointOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether endpoint auto-mapping is enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the route prefix for read-only diagnostics endpoints.
    /// </summary>
    public string Prefix { get; set; } = "/vapecache";

    /// <summary>
    /// Gets or sets a value indicating whether breaker control endpoints are mapped.
    /// When enabled, control routes are mapped under <see cref="AdminPrefix"/>.
    /// </summary>
    public bool IncludeBreakerControlEndpoints { get; set; }

    /// <summary>
    /// Gets or sets the route prefix for admin-only control endpoints.
    /// </summary>
    public string AdminPrefix { get; set; } = "/vapecache/admin";

    /// <summary>
    /// Gets or sets a value indicating whether the auto-mapped admin control group requires authorization.
    /// </summary>
    public bool RequireAuthorizationOnAdminEndpoints { get; set; }

    /// <summary>
    /// Gets or sets the authorization policy applied to the auto-mapped admin control group.
    /// </summary>
    public string? AdminAuthorizationPolicy { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether intent inspection endpoints are mapped.
    /// </summary>
    public bool IncludeIntentEndpoints { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the live SSE stream endpoint is mapped.
    /// </summary>
    public bool EnableLiveStream { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the built-in dashboard endpoint is mapped.
    /// </summary>
    public bool EnableDashboard { get; set; }

    /// <summary>
    /// Gets or sets the sampling interval used by the live metrics feed.
    /// </summary>
    public TimeSpan LiveSampleInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets the bounded channel capacity for live metrics samples.
    /// </summary>
    public int LiveChannelCapacity { get; set; } = 256;
}
