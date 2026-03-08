namespace VapeCache.Extensions.Aspire;

/// <summary>
/// Options for automatic mapping of VapeCache operational endpoints.
/// </summary>
public sealed class VapeCacheEndpointOptions
{
    public bool Enabled { get; set; } = false;
    public string Prefix { get; set; } = "/vapecache";
    public bool IncludeBreakerControlEndpoints { get; set; } = false;
    public bool IncludeIntentEndpoints { get; set; } = false;
    public bool EnableLiveStream { get; set; } = false;
    public bool EnableDashboard { get; set; } = false;
    public TimeSpan LiveSampleInterval { get; set; } = TimeSpan.FromSeconds(1);
    public int LiveChannelCapacity { get; set; } = 256;
}
