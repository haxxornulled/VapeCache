namespace VapeCache.Licensing.ControlPlane.Revocation;

/// <summary>
/// Runtime options for the licensing revocation control-plane service.
/// </summary>
public sealed class RevocationControlPlaneOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "RevocationControlPlane";

    /// <summary>
    /// When true, all revocation endpoints require API-key authentication.
    /// </summary>
    public bool RequireApiKey { get; set; } = true;

    /// <summary>
    /// Header name used to provide the control-plane API key.
    /// </summary>
    public string ApiKeyHeaderName { get; set; } = "X-VapeCache-ApiKey";

    /// <summary>
    /// Shared secret for control-plane API authentication.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// State file path used for persistence across restarts.
    /// Relative paths are resolved from the app base directory.
    /// </summary>
    public string PersistencePath { get; set; } = "data/revocations-state.json";
}
