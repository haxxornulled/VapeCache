namespace VapeCache.Extensions.Aspire;

/// <summary>
/// Options for automatic mapping of VapeCache operational endpoints.
/// </summary>
public sealed class VapeCacheEndpointOptions
{
    /// <summary>
    /// Gets or sets the enabled.
    /// </summary>
    public bool Enabled { get; set; } = false;
    /// <summary>
    /// Gets or sets the prefix.
    /// </summary>
    public string Prefix { get; set; } = "/vapecache";
    /// <summary>
    /// Gets or sets the nclude breaker control endpoints.
    /// </summary>
    public bool IncludeBreakerControlEndpoints { get; set; } = false;
    /// <summary>
    /// Gets or sets the nclude intent endpoints.
    /// </summary>
    public bool IncludeIntentEndpoints { get; set; } = false;
    /// <summary>
    /// Gets or sets the enable live stream.
    /// </summary>
    public bool EnableLiveStream { get; set; } = false;
    /// <summary>
    /// Gets or sets the enable dashboard.
    /// </summary>
    public bool EnableDashboard { get; set; } = false;
    /// <summary>
    /// Executes from seconds.
    /// </summary>
    public TimeSpan LiveSampleInterval { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>
    /// Gets or sets the live channel capacity.
    /// </summary>
    public int LiveChannelCapacity { get; set; } = 256;
}
