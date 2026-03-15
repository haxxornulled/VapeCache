namespace VapeCache.Abstractions.Connections;

/// <summary>
/// Enterprise capability gate used by runtime components to decide whether
/// enterprise-only features may execute.
/// </summary>
public interface IEnterpriseFeatureGate
{
    /// <summary>
    /// Gets a value indicating whether autoscaling is licensed for this runtime.
    /// </summary>
    bool IsAutoscalerLicensed { get; }

    /// <summary>
    /// Gets a value indicating whether durable spill persistence is licensed for this runtime.
    /// </summary>
    bool IsDurableSpillLicensed { get; }

    /// <summary>
    /// Gets a value indicating whether reconciliation features are licensed for this runtime.
    /// </summary>
    bool IsReconciliationLicensed { get; }
}
