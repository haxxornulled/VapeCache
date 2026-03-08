namespace VapeCache.Extensions.AspNetCore;

/// <summary>
/// Options for emitting sticky-session affinity hints while Redis failover is active.
/// </summary>
public sealed class VapeCacheFailoverAffinityOptions
{
    /// <summary>
    /// Enables affinity hint middleware.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Unique node identifier emitted in response headers/cookies.
    /// </summary>
    public string NodeId { get; set; } = $"{Environment.MachineName}:{Environment.ProcessId}";

    /// <summary>
    /// Header used to expose the current failover node id.
    /// </summary>
    public string NodeHeaderName { get; set; } = "X-VapeCache-Node";

    /// <summary>
    /// Header used to expose current breaker/fallback state.
    /// </summary>
    public string StateHeaderName { get; set; } = "X-VapeCache-Failover-State";

    /// <summary>
    /// Sticky-session cookie name used for affinity hints.
    /// </summary>
    public string CookieName { get; set; } = "VapeCacheAffinity";

    /// <summary>
    /// Cookie TTL used when the middleware sets affinity hints.
    /// </summary>
    public TimeSpan CookieTtl { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Sets affinity cookies only when failover is active.
    /// </summary>
    public bool SetCookieOnlyWhenFailingOver { get; set; } = true;

    /// <summary>
    /// Emits a mismatch header when the request cookie does not match this node id.
    /// </summary>
    public bool EmitMismatchHeader { get; set; } = true;
}
