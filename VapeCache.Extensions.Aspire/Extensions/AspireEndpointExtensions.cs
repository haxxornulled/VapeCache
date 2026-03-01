using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Text.Json;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using System.Linq;

namespace VapeCache.Extensions.Aspire;

/// <summary>
/// HTTP endpoint mapping helpers for wrapper hosts (ASP.NET Core, plugin gateways, edge APIs).
/// Keeps core VapeCache transport-agnostic while providing a clean diagnostics/control surface.
/// </summary>
public static class AspireEndpointExtensions
{
    /// <summary>
    /// Maps VapeCache diagnostics endpoints under a route prefix.
    /// By default this maps read-only endpoints; breaker control endpoints are opt-in.
    /// </summary>
    /// <param name="endpoints">Endpoint route builder.</param>
    /// <param name="prefix">Route prefix (default: /vapecache).</param>
    /// <param name="includeBreakerControlEndpoints">
    /// When true, maps POST endpoints for force-open/clear breaker operations.
    /// Keep disabled by default and secure these routes in production.
    /// </param>
    /// <returns>A route group builder for additional customization.</returns>
    public static RouteGroupBuilder MapVapeCacheEndpoints(
        this IEndpointRouteBuilder endpoints,
        string prefix = "/vapecache",
        bool includeBreakerControlEndpoints = false,
        bool includeLiveStreamEndpoint = true,
        bool includeIntentEndpoints = true)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (string.IsNullOrWhiteSpace(prefix))
            throw new ArgumentException("Endpoint prefix is required.", nameof(prefix));

        var normalizedPrefix = NormalizePrefix(prefix);
        var group = endpoints.MapGroup(normalizedPrefix)
            .WithTags("VapeCache");

        group.MapGet("/status", static (
            ICurrentCacheService current,
            ICacheStats stats,
            IRedisCircuitBreakerState breaker,
            IRedisFailoverController failover,
            ISpillStoreDiagnostics? spillDiagnostics,
            IEnumerable<IRedisMultiplexerDiagnostics> diagnostics) =>
        {
            var snapshot = stats.Snapshot;
            var hitRate = ComputeHitRate(snapshot);
            var diagnostic = diagnostics.FirstOrDefault();
            var autoscaler = diagnostic?.GetAutoscalerSnapshot();
            var lanes = diagnostic?.GetMuxLaneSnapshots();
            var spill = spillDiagnostics?.GetSnapshot();

            var response = new VapeCacheEndpointStatusResponse(
                TimestampUtc: DateTimeOffset.UtcNow,
                CurrentBackend: current.CurrentName,
                Stats: new VapeCacheEndpointStatsResponse(
                    GetCalls: snapshot.GetCalls,
                    Hits: snapshot.Hits,
                    Misses: snapshot.Misses,
                    SetCalls: snapshot.SetCalls,
                    RemoveCalls: snapshot.RemoveCalls,
                    FallbackToMemory: snapshot.FallbackToMemory,
                    RedisBreakerOpened: snapshot.RedisBreakerOpened,
                    StampedeKeyRejected: snapshot.StampedeKeyRejected,
                    StampedeLockWaitTimeout: snapshot.StampedeLockWaitTimeout,
                    StampedeFailureBackoffRejected: snapshot.StampedeFailureBackoffRejected,
                    HitRate: hitRate,
                    Spill: spill,
                    Autoscaler: autoscaler,
                    Lanes: lanes),
                CircuitBreaker: new VapeCacheEndpointBreakerResponse(
                    Enabled: breaker.Enabled,
                    IsOpen: breaker.IsOpen,
                    ConsecutiveFailures: breaker.ConsecutiveFailures,
                    OpenRemaining: breaker.OpenRemaining,
                    HalfOpenProbeInFlight: breaker.HalfOpenProbeInFlight,
                    IsForcedOpen: failover.IsForcedOpen,
                    Reason: failover.Reason),
                Spill: spill,
                Autoscaler: autoscaler,
                Lanes: lanes);

            return Results.Ok(response);
        })
        .WithName("VapeCacheStatus");

        group.MapGet("/stats", static (ICacheStats stats, ISpillStoreDiagnostics? spillDiagnostics, IEnumerable<IRedisMultiplexerDiagnostics> diagnostics) =>
        {
            var snapshot = stats.Snapshot;
            var hitRate = ComputeHitRate(snapshot);
            var diagnostic = diagnostics.FirstOrDefault();
            var autoscaler = diagnostic?.GetAutoscalerSnapshot();
            var lanes = diagnostic?.GetMuxLaneSnapshots();
            var spill = spillDiagnostics?.GetSnapshot();
            var response = new VapeCacheEndpointStatsResponse(
                GetCalls: snapshot.GetCalls,
                Hits: snapshot.Hits,
                Misses: snapshot.Misses,
                SetCalls: snapshot.SetCalls,
                RemoveCalls: snapshot.RemoveCalls,
                FallbackToMemory: snapshot.FallbackToMemory,
                RedisBreakerOpened: snapshot.RedisBreakerOpened,
                StampedeKeyRejected: snapshot.StampedeKeyRejected,
                StampedeLockWaitTimeout: snapshot.StampedeLockWaitTimeout,
                StampedeFailureBackoffRejected: snapshot.StampedeFailureBackoffRejected,
                HitRate: hitRate,
                Spill: spill,
                Autoscaler: autoscaler,
                Lanes: lanes);
            return Results.Ok(response);
        })
        .WithName("VapeCacheStats");

        if (includeIntentEndpoints)
        {
            group.MapGet("/intent/{key}", static (string key, ICacheIntentRegistry intentRegistry) =>
            {
                if (intentRegistry.TryGet(key, out var entry))
                    return Results.Ok(entry);
                return Results.NotFound();
            })
            .WithName("VapeCacheIntentByKey");

            group.MapGet("/intent", static (int? take, ICacheIntentRegistry intentRegistry) =>
            {
                var max = Math.Clamp(take ?? 50, 1, 500);
                return Results.Ok(intentRegistry.GetRecent(max));
            })
            .WithName("VapeCacheIntentRecent");
        }

        if (includeLiveStreamEndpoint)
        {
            group.MapGet("/stream", static async (HttpContext httpContext, IEnumerable<IVapeCacheLiveMetricsFeed> feeds) =>
            {
                var feed = feeds.FirstOrDefault();
                if (feed is null)
                    return Results.NotFound();

                httpContext.Response.StatusCode = StatusCodes.Status200OK;
                httpContext.Response.Headers.CacheControl = "no-cache";
                httpContext.Response.Headers.Connection = "keep-alive";
                httpContext.Response.ContentType = "text/event-stream";

                var reader = feed.Subscribe(httpContext.RequestAborted);
                await foreach (var sample in reader.ReadAllAsync(httpContext.RequestAborted).ConfigureAwait(false))
                {
                    var json = JsonSerializer.Serialize(sample);
                    await httpContext.Response.WriteAsync("event: vapecache-stats\n", httpContext.RequestAborted).ConfigureAwait(false);
                    await httpContext.Response.WriteAsync($"data: {json}\n\n", httpContext.RequestAborted).ConfigureAwait(false);
                    await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted).ConfigureAwait(false);
                }

                return Results.Empty;
            })
            .WithName("VapeCacheLiveStream");
        }

        if (includeBreakerControlEndpoints)
        {
            group.MapPost("/breaker/force-open", static (
                VapeCacheForceOpenRequest? request,
                IRedisFailoverController failover) =>
            {
                var reason = string.IsNullOrWhiteSpace(request?.Reason)
                    ? "manual-force-open"
                    : request.Reason.Trim();

                failover.ForceOpen(reason);
                return Results.Ok(new VapeCacheBreakerControlResponse(
                    IsForcedOpen: failover.IsForcedOpen,
                    Reason: failover.Reason));
            })
            .WithName("VapeCacheBreakerForceOpen");

            group.MapPost("/breaker/clear", static (IRedisFailoverController failover) =>
            {
                failover.ClearForcedOpen();
                return Results.Ok(new VapeCacheBreakerControlResponse(
                    IsForcedOpen: failover.IsForcedOpen,
                    Reason: failover.Reason));
            })
            .WithName("VapeCacheBreakerClear");
        }

        return group;
    }

    private static double ComputeHitRate(CacheStatsSnapshot snapshot)
    {
        var totalReads = snapshot.Hits + snapshot.Misses;
        return totalReads <= 0 ? 0d : (double)snapshot.Hits / totalReads;
    }

    private static string NormalizePrefix(string prefix)
    {
        var normalized = prefix.Trim();
        if (!normalized.StartsWith('/'))
            normalized = "/" + normalized;
        normalized = normalized.TrimEnd('/');
        return string.IsNullOrEmpty(normalized) ? "/" : normalized;
    }
}

/// <summary>
/// Status payload for VapeCache runtime diagnostics endpoint.
/// </summary>
public sealed record VapeCacheEndpointStatusResponse(
    DateTimeOffset TimestampUtc,
    string CurrentBackend,
    VapeCacheEndpointStatsResponse Stats,
    VapeCacheEndpointBreakerResponse CircuitBreaker,
    SpillStoreDiagnosticsSnapshot? Spill,
    RedisAutoscalerSnapshot? Autoscaler,
    IReadOnlyList<RedisMuxLaneSnapshot>? Lanes = null);

/// <summary>
/// Cache stats payload exposed by endpoint wrappers.
/// </summary>
public sealed record VapeCacheEndpointStatsResponse(
    long GetCalls,
    long Hits,
    long Misses,
    long SetCalls,
    long RemoveCalls,
    long FallbackToMemory,
    long RedisBreakerOpened,
    long StampedeKeyRejected,
    long StampedeLockWaitTimeout,
    long StampedeFailureBackoffRejected,
    double HitRate,
    SpillStoreDiagnosticsSnapshot? Spill,
    RedisAutoscalerSnapshot? Autoscaler,
    IReadOnlyList<RedisMuxLaneSnapshot>? Lanes = null);

/// <summary>
/// Circuit breaker status payload exposed by endpoint wrappers.
/// </summary>
public sealed record VapeCacheEndpointBreakerResponse(
    bool Enabled,
    bool IsOpen,
    int ConsecutiveFailures,
    TimeSpan? OpenRemaining,
    bool HalfOpenProbeInFlight,
    bool IsForcedOpen,
    string? Reason);

/// <summary>
/// Request payload to force-open the circuit breaker.
/// </summary>
public sealed record VapeCacheForceOpenRequest(string? Reason);

/// <summary>
/// Response payload for breaker control operations.
/// </summary>
public sealed record VapeCacheBreakerControlResponse(
    bool IsForcedOpen,
    string? Reason);
