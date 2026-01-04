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
    /// Pro tier - $29/month, max 3 production instances.
    /// Includes reconciliation, Redis modules, advanced telemetry.
    /// </summary>
    Pro = 1,

    /// <summary>
    /// Enterprise tier - $299/month, unlimited instances.
    /// Includes all Pro features plus multi-region, compliance, source code access.
    /// </summary>
    Enterprise = 2
}
