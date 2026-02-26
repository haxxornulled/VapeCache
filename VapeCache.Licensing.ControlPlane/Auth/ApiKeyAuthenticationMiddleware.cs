using Microsoft.Extensions.Options;
using VapeCache.Licensing.ControlPlane.Revocation;

namespace VapeCache.Licensing.ControlPlane.Auth;

/// <summary>
/// Enforces API-key authentication for revocation endpoints.
/// </summary>
public sealed class ApiKeyAuthenticationMiddleware(
    RequestDelegate next,
    ILogger<ApiKeyAuthenticationMiddleware> logger)
{
    private static readonly PathString RevocationPathPrefix = new("/api/v1/revocations");

    /// <summary>
    /// Executes middleware logic for the current request.
    /// </summary>
    public async Task InvokeAsync(
        HttpContext context,
        IApiKeyAuthorizer authorizer,
        IOptionsMonitor<RevocationControlPlaneOptions> optionsMonitor)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizer);
        ArgumentNullException.ThrowIfNull(optionsMonitor);

        if (!context.Request.Path.StartsWithSegments(RevocationPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (!authorizer.IsAuthorized(context, out var failureReason))
        {
            logger.LogWarning(
                "Revocation endpoint request rejected. Path={Path} Reason={Reason} RequireApiKey={RequireApiKey}",
                context.Request.Path,
                failureReason,
                optionsMonitor.CurrentValue.RequireApiKey);

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(
                new ProblemDetailsPayload(
                    "Unauthorized",
                    "API key validation failed for the licensing control-plane endpoint.",
                    failureReason),
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    private sealed record ProblemDetailsPayload(
        string Title,
        string Detail,
        string ReasonCode);
}
