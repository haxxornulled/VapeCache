using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;

namespace VapeCache.Extensions.AspNetCore;

/// <summary>
/// Emits node-affinity hints for sticky-session routing during local in-memory failover.
/// </summary>
public sealed class VapeCacheFailoverAffinityMiddleware(
    RequestDelegate next,
    IOptionsMonitor<VapeCacheFailoverAffinityOptions> optionsMonitor,
    IRedisCircuitBreakerState breakerState,
    ILogger<VapeCacheFailoverAffinityMiddleware> logger)
{
    /// <summary>
    /// Executes middleware logic for the current request.
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = optionsMonitor.CurrentValue;
        if (!options.Enabled)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var nodeId = string.IsNullOrWhiteSpace(options.NodeId)
            ? $"{Environment.MachineName}:{Environment.ProcessId}"
            : options.NodeId.Trim();
        var isFailingOver = breakerState.Enabled && breakerState.IsOpen;
        var incomingNode = context.Request.Cookies.TryGetValue(options.CookieName, out var cookieNode)
            ? cookieNode
            : null;

        context.Response.Headers[options.NodeHeaderName] = nodeId;
        context.Response.Headers[options.StateHeaderName] = isFailingOver ? "fallback-open" : "redis-healthy";

        var shouldSetCookie = !options.SetCookieOnlyWhenFailingOver || isFailingOver;
        if (shouldSetCookie)
        {
            context.Response.Cookies.Append(
                options.CookieName,
                nodeId,
                new CookieOptions
                {
                    HttpOnly = false,
                    IsEssential = true,
                    MaxAge = options.CookieTtl,
                    SameSite = SameSiteMode.Lax,
                    Path = "/"
                });
        }

        if (options.EmitMismatchHeader &&
            !string.IsNullOrWhiteSpace(incomingNode) &&
            !string.Equals(incomingNode, nodeId, StringComparison.Ordinal))
        {
            context.Response.Headers["X-VapeCache-Affinity-Mismatch"] = "1";
            logger.LogDebug(
                "Affinity mismatch detected. IncomingNode={IncomingNode} CurrentNode={CurrentNode} Path={Path}",
                incomingNode,
                nodeId,
                context.Request.Path);
        }

        await next(context).ConfigureAwait(false);
    }
}
