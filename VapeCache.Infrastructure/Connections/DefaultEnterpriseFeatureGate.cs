using VapeCache.Abstractions.Connections;

namespace VapeCache.Infrastructure.Connections;

/// <summary>
/// OSS default enterprise gate. Enterprise modules can override this registration.
/// </summary>
internal sealed class DefaultEnterpriseFeatureGate : IEnterpriseFeatureGate
{
    public bool IsAutoscalerLicensed => false;
    public bool IsDurableSpillLicensed => false;
    public bool IsReconciliationLicensed => false;
}
