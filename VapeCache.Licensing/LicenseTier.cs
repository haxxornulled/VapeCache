namespace VapeCache.Licensing;

/// <summary>
/// Represents the VapeCache license tier.
/// </summary>
public enum LicenseTier
{
    /// <summary>
    /// Free tier - MIT licensed core features only.
    /// </summary>
    Free = 0,

    /// <summary>
    /// Pro tier - $99/month, max 5 production instances.
    /// Includes Redis modules and advanced telemetry (NO reconciliation).
    /// </summary>
    Pro = 1,

    /// <summary>
    /// Enterprise tier - $499/month, unlimited instances.
    /// Includes reconciliation (zero data loss), multi-region, compliance, source code access.
    /// </summary>
    Enterprise = 2
}
