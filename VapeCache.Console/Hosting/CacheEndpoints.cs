using System.Buffers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using VapeCache.Abstractions.Caching;

namespace VapeCache.Console.Hosting;

public sealed class CacheEndpoints
{
    private CacheEndpoints() { }

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/healthz", static () => Results.Ok(new { status = "ok" }));

        endpoints.MapGet("/cache/current", static (ICurrentCacheService current) =>
            Results.Ok(new { current = current.CurrentName }));

        endpoints.MapGet("/cache/breaker", static (IServiceProvider sp) =>
        {
            var state = sp.GetService<IRedisCircuitBreakerState>();
            if (state is null) return Results.NotFound();
            return Results.Ok(new
            {
                state.Enabled,
                state.IsOpen,
                state.ConsecutiveFailures,
                openRemainingMs = state.OpenRemaining?.TotalMilliseconds,
                state.HalfOpenProbeInFlight
            });
        });

        endpoints.MapGet("/cache/stats", static (ICacheStats stats) =>
            Results.Ok(stats.Snapshot));

        endpoints.MapGet("/cache/{key}", async (string key, HttpContext ctx) =>
        {
            var cache = ctx.RequestServices.GetRequiredService<ICacheService>();
            var current = ctx.RequestServices.GetRequiredService<ICurrentCacheService>();

            var bytes = await cache.GetAsync(key, ctx.RequestAborted).ConfigureAwait(false);
            ctx.Response.Headers["X-Cache-Backend"] = current.CurrentName;
            if (bytes is null) return Results.NotFound();
            return Results.Bytes(bytes, "application/octet-stream");
        });

        endpoints.MapPut("/cache/{key}", async (string key, HttpRequest req, HttpContext ctx) =>
        {
            var cache = ctx.RequestServices.GetRequiredService<ICacheService>();
            var current = ctx.RequestServices.GetRequiredService<ICurrentCacheService>();

            var ttlSeconds = req.Query.TryGetValue("ttlSeconds", out var ttlStr) && int.TryParse(ttlStr, out var ttlInt)
                ? (TimeSpan?)TimeSpan.FromSeconds(Math.Max(1, ttlInt))
                : null;

            using var ms = new MemoryStream();
            await req.Body.CopyToAsync(ms, ctx.RequestAborted).ConfigureAwait(false);
            await cache.SetAsync(key, ms.ToArray(), new CacheEntryOptions(ttlSeconds), ctx.RequestAborted).ConfigureAwait(false);

            ctx.Response.Headers["X-Cache-Backend"] = current.CurrentName;
            return Results.Ok();
        });

        endpoints.MapDelete("/cache/{key}", async (string key, HttpContext ctx) =>
        {
            var cache = ctx.RequestServices.GetRequiredService<ICacheService>();
            var current = ctx.RequestServices.GetRequiredService<ICurrentCacheService>();
            var ok = await cache.RemoveAsync(key, ctx.RequestAborted).ConfigureAwait(false);
            ctx.Response.Headers["X-Cache-Backend"] = current.CurrentName;
            return Results.Ok(new { removed = ok });
        });

        // Simple demo endpoint: GET-or-SET using provided text payload if missing.
        endpoints.MapPost("/cache/{key}/get-or-set", async (string key, HttpRequest req, HttpContext ctx) =>
        {
            var cache = ctx.RequestServices.GetRequiredService<ICacheService>();
            var current = ctx.RequestServices.GetRequiredService<ICurrentCacheService>();

            var ttlSeconds = req.Query.TryGetValue("ttlSeconds", out var ttlStr) && int.TryParse(ttlStr, out var ttlInt)
                ? (TimeSpan?)TimeSpan.FromSeconds(Math.Max(1, ttlInt))
                : null;

            using var reader = new StreamReader(req.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            var payload = await reader.ReadToEndAsync(ctx.RequestAborted).ConfigureAwait(false);
            if (payload.Length == 0) payload = "default";

            static void Serialize(IBufferWriter<byte> w, string v)
            {
                var bytes = Encoding.UTF8.GetBytes(v);
                var span = w.GetSpan(bytes.Length);
                bytes.CopyTo(span);
                w.Advance(bytes.Length);
            }

            static string Deserialize(ReadOnlySpan<byte> s) => Encoding.UTF8.GetString(s);

            var value = await cache.GetOrSetAsync(
                    key,
                    _ => ValueTask.FromResult(payload),
                    Serialize,
                    Deserialize,
                    new CacheEntryOptions(ttlSeconds),
                    ctx.RequestAborted)
                .ConfigureAwait(false);

            ctx.Response.Headers["X-Cache-Backend"] = current.CurrentName;
            return Results.Ok(new { value, backend = current.CurrentName });
        });
    }

    public static void Configure(IApplicationBuilder app)
    {
        app.UseRouting();
        app.UseEndpoints(Map);
    }
}
