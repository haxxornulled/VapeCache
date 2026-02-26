namespace VapeCache.Licensing.ControlPlane.Revocation;

/// <summary>
/// Stores and evaluates license revocation and kill-switch state.
/// </summary>
public interface IRevocationRegistry
{
    /// <summary>
    /// Evaluates effective revocation status using license, organization, and key-id scopes.
    /// </summary>
    RevocationDecision Evaluate(string licenseId, string? organizationId, string? keyId);

    /// <summary>
    /// Adds or updates a license-level revocation.
    /// </summary>
    RevocationMutationResult RevokeLicense(string licenseId, string reason, string actor);

    /// <summary>
    /// Removes a license-level revocation.
    /// </summary>
    RevocationMutationResult ActivateLicense(string licenseId, string reason, string actor);

    /// <summary>
    /// Enables organization-level kill-switch.
    /// </summary>
    RevocationMutationResult EnableOrganizationKillSwitch(string organizationId, string reason, string actor);

    /// <summary>
    /// Disables organization-level kill-switch.
    /// </summary>
    RevocationMutationResult DisableOrganizationKillSwitch(string organizationId, string reason, string actor);

    /// <summary>
    /// Adds or updates a key-id-level revocation.
    /// </summary>
    RevocationMutationResult RevokeKeyId(string keyId, string reason, string actor);

    /// <summary>
    /// Removes a key-id-level revocation.
    /// </summary>
    RevocationMutationResult ActivateKeyId(string keyId, string reason, string actor);

    /// <summary>
    /// Returns a point-in-time snapshot of all revocation data.
    /// </summary>
    RevocationSnapshot GetSnapshot();
}
