namespace VapeCache.Licensing;

/// <summary>
/// Result of an online revocation/kill-switch check.
/// </summary>
public sealed record LicenseRevocationCheckResult(
    bool IsAllowed,
    bool IsRevoked,
    string Reason)
{
    public static LicenseRevocationCheckResult Allowed(string reason = "active")
        => new(IsAllowed: true, IsRevoked: false, reason);

    public static LicenseRevocationCheckResult Revoked(string reason)
        => new(IsAllowed: false, IsRevoked: true, reason);
}
