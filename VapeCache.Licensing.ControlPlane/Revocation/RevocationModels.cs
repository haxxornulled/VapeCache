namespace VapeCache.Licensing.ControlPlane.Revocation;

/// <summary>
/// Request payload for revoke/activate mutations.
/// </summary>
public sealed record RevocationMutationRequest(
    string? Reason,
    string? Actor);

/// <summary>
/// Result of a revocation mutation operation.
/// </summary>
public sealed record RevocationMutationResult(
    string Scope,
    string Identity,
    bool IsRevoked,
    bool Changed,
    string Reason,
    string Actor,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Effective revocation decision for a given license context.
/// </summary>
public sealed record RevocationDecision(
    bool Revoked,
    string Reason,
    string Source,
    DateTimeOffset? UpdatedAtUtc);

/// <summary>
/// Revocation record stored by scope.
/// </summary>
public sealed record RevocationRecord(
    string Identity,
    string Reason,
    string Actor,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Full revocation snapshot used by operator diagnostics.
/// </summary>
public sealed record RevocationSnapshot(
    IReadOnlyList<RevocationRecord> RevokedLicenses,
    IReadOnlyList<RevocationRecord> OrganizationKillSwitches,
    IReadOnlyList<RevocationRecord> RevokedKeyIds);

/// <summary>
/// Status response returned by the status endpoint.
/// </summary>
public sealed record RevocationStatusResponse(
    string LicenseId,
    string? OrganizationId,
    string? KeyId,
    bool Revoked,
    string Reason,
    string Source,
    DateTimeOffset? UpdatedAtUtc)
{
    /// <summary>
    /// Creates a response from a decision object.
    /// </summary>
    public static RevocationStatusResponse From(
        string licenseId,
        string? organizationId,
        string? keyId,
        RevocationDecision decision)
        => new(
            licenseId,
            organizationId,
            keyId,
            decision.Revoked,
            decision.Reason,
            decision.Source,
            decision.UpdatedAtUtc);
}

/// <summary>
/// Mutation response returned by revoke/activate endpoints.
/// </summary>
public sealed record RevocationMutationResponse(
    string Scope,
    string Identity,
    bool Revoked,
    bool Changed,
    string Reason,
    string Actor,
    DateTimeOffset UpdatedAtUtc)
{
    /// <summary>
    /// Creates an API response from an internal mutation result.
    /// </summary>
    public static RevocationMutationResponse From(RevocationMutationResult result)
        => new(
            result.Scope,
            result.Identity,
            result.IsRevoked,
            result.Changed,
            result.Reason,
            result.Actor,
            result.UpdatedAtUtc);
}

/// <summary>
/// Snapshot response returned by the snapshot endpoint.
/// </summary>
public sealed record RevocationSnapshotResponse(
    IReadOnlyList<RevocationRecord> RevokedLicenses,
    IReadOnlyList<RevocationRecord> OrganizationKillSwitches,
    IReadOnlyList<RevocationRecord> RevokedKeyIds,
    DateTimeOffset GeneratedAtUtc)
{
    /// <summary>
    /// Creates a response from the current registry snapshot.
    /// </summary>
    public static RevocationSnapshotResponse From(RevocationSnapshot snapshot)
        => new(
            snapshot.RevokedLicenses,
            snapshot.OrganizationKillSwitches,
            snapshot.RevokedKeyIds,
            DateTimeOffset.UtcNow);
}
