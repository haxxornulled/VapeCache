namespace VapeCache.Extensions.Aspire;

/// <summary>
/// Startup warmup and readiness options for Aspire-hosted VapeCache services.
/// </summary>
public sealed record VapeCacheStartupWarmupOptions
{
    /// <summary>
    /// Enables startup warmup. Disabled by default and activated when <c>WithStartupWarmup</c> is used.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Number of pooled Redis leases to acquire and return during warmup.
    /// </summary>
    public int ConnectionsToWarm { get; set; } = 8;

    /// <summary>
    /// Minimum successful warmup leases required to mark readiness healthy.
    /// </summary>
    public int RequiredSuccessfulConnections { get; set; } = 4;

    /// <summary>
    /// Per-startup warmup timeout.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// When true, sends PING on each warmed lease to validate server responsiveness.
    /// </summary>
    public bool ValidatePing { get; set; } = true;

    /// <summary>
    /// When true, throws during startup if readiness is not achieved.
    /// </summary>
    public bool FailFastOnWarmupFailure { get; set; } = false;
}
