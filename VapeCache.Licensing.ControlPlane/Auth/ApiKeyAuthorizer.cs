using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using VapeCache.Licensing.ControlPlane.Revocation;

namespace VapeCache.Licensing.ControlPlane.Auth;

/// <summary>
/// API-key authorizer for revocation control-plane endpoints.
/// </summary>
public sealed class ApiKeyAuthorizer(IOptionsMonitor<RevocationControlPlaneOptions> optionsMonitor) : IApiKeyAuthorizer
{
    /// <inheritdoc />
    public bool IsAuthorized(HttpContext context, out string failureReason)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = optionsMonitor.CurrentValue;
        if (!options.RequireApiKey)
        {
            failureReason = "api-key-disabled";
            return true;
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            failureReason = "api-key-not-configured";
            return false;
        }

        if (!context.Request.Headers.TryGetValue(options.ApiKeyHeaderName, out var candidate) ||
            string.IsNullOrWhiteSpace(candidate))
        {
            failureReason = "api-key-missing";
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(options.ApiKey.Trim());
        var candidateBytes = Encoding.UTF8.GetBytes(candidate.ToString().Trim());
        if (expectedBytes.Length != candidateBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(expectedBytes, candidateBytes))
        {
            failureReason = "api-key-invalid";
            return false;
        }

        failureReason = "ok";
        return true;
    }
}
