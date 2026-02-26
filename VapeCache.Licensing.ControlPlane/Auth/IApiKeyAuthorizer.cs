namespace VapeCache.Licensing.ControlPlane.Auth;

/// <summary>
/// Validates API-key authentication for control-plane endpoints.
/// </summary>
public interface IApiKeyAuthorizer
{
    /// <summary>
    /// Returns true when the request is authorized to access revocation endpoints.
    /// </summary>
    bool IsAuthorized(HttpContext context, out string failureReason);
}
