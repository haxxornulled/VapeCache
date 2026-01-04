namespace VapeCache.Licensing;

/// <summary>
/// Represents the VapeCache license tier.
/// Application-based licensing - no per-server or per-cluster limits.
/// </summary>
public enum LicenseTier
{
    /// <summary>
    /// Free tier - MIT licensed core features.
    /// Unlimited production deployments, any Redis topology (standalone/sentinel/cluster).
    /// </summary>
    Free = 0,

    /// <summary>
    /// Enterprise tier - $499/month per organization.
    /// Unlimited production deployments, unlimited Redis topology.
    /// Includes Persistence (spill-to-disk) and Reconciliation (zero data loss).
    /// Priority support, SLA guarantees, source code access.
    /// </summary>
    Enterprise = 2
}
