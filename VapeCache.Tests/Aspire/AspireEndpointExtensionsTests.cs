using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using VapeCache.Abstractions.Caching;
using VapeCache.Extensions.Aspire;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Tests.Aspire;

public sealed class AspireEndpointExtensionsTests
{
    [Fact]
    public async Task MapVapeCacheEndpoints_MapsReadOnlyDiagnostics()
    {
        await using var app = await CreateAppAsync(includeBreakerControlEndpoints: false);
        using var client = app.GetTestClient();

        var statusResponse = await client.GetAsync("/vapecache/status");
        var statsResponse = await client.GetAsync("/vapecache/stats");

        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, statsResponse.StatusCode);

        var status = await statusResponse.Content.ReadFromJsonAsync<VapeCacheEndpointStatusResponse>();
        var stats = await statsResponse.Content.ReadFromJsonAsync<VapeCacheEndpointStatsResponse>();

        Assert.NotNull(status);
        Assert.NotNull(stats);
        Assert.Equal("redis", status!.CurrentBackend);
        Assert.Equal(0, stats!.GetCalls);
        Assert.Equal(0d, stats.HitRate);
        Assert.NotNull(status.Spill);
        Assert.NotNull(stats.Spill);
        Assert.Equal("noop", status.Spill!.Mode);
        Assert.NotNull(status.Lanes);
        Assert.NotNull(stats.Lanes);
        Assert.NotEmpty(status.Lanes!);
        Assert.NotEmpty(stats.Lanes!);
    }

    [Fact]
    public async Task MapVapeCacheEndpoints_DoesNotMapBreakerControl_WhenDisabled()
    {
        await using var app = await CreateAppAsync(includeBreakerControlEndpoints: false);
        using var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync(
            "/vapecache/breaker/force-open",
            new VapeCacheForceOpenRequest("test"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task MapVapeCacheEndpoints_MapsBreakerControl_WhenEnabled()
    {
        await using var app = await CreateAppAsync(includeBreakerControlEndpoints: true);
        using var client = app.GetTestClient();

        var open = await client.PostAsJsonAsync(
            "/vapecache/breaker/force-open",
            new VapeCacheForceOpenRequest("integration-test"));
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);

        var afterOpen = await client.GetFromJsonAsync<VapeCacheEndpointStatusResponse>("/vapecache/status");
        Assert.NotNull(afterOpen);
        Assert.True(afterOpen!.CircuitBreaker.IsForcedOpen);
        Assert.Equal("integration-test", afterOpen.CircuitBreaker.Reason);

        var clear = await client.PostAsync("/vapecache/breaker/clear", content: null);
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);

        var afterClear = await client.GetFromJsonAsync<VapeCacheEndpointStatusResponse>("/vapecache/status");
        Assert.NotNull(afterClear);
        Assert.False(afterClear!.CircuitBreaker.IsForcedOpen);
        Assert.Null(afterClear.CircuitBreaker.Reason);
    }

    [Fact]
    public async Task MapVapeCacheEndpoints_NormalizesPrefix()
    {
        await using var app = await CreateAppAsync(includeBreakerControlEndpoints: false, prefix: "cache-admin/");
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/cache-admin/stats");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task MapVapeCacheEndpoints_ExposesIntentEndpoints()
    {
        await using var app = await CreateAppAsync(includeBreakerControlEndpoints: false);
        using var client = app.GetTestClient();

        var setResponse = await client.PostAsJsonAsync("/seed-intent", new { });
        Assert.Equal(HttpStatusCode.OK, setResponse.StatusCode);

        var recent = await client.GetAsync("/vapecache/intent?take=10");
        var byKey = await client.GetAsync("/vapecache/intent/intent-endpoint-key");
        var missing = await client.GetAsync("/vapecache/intent/not-found");

        Assert.Equal(HttpStatusCode.OK, recent.StatusCode);
        Assert.Equal(HttpStatusCode.OK, byKey.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task MapVapeCacheEndpoints_CanDisableIntentEndpoints()
    {
        await using var app = await CreateAppAsync(includeBreakerControlEndpoints: false, includeIntentEndpoints: false);
        using var client = app.GetTestClient();

        var byKey = await client.GetAsync("/vapecache/intent/some-key");
        var recent = await client.GetAsync("/vapecache/intent");

        Assert.Equal(HttpStatusCode.NotFound, byKey.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, recent.StatusCode);
    }

    [Fact]
    public async Task WithAutoMappedEndpoints_MapsEndpoints_WithoutProgramRouteSetup()
    {
        await using var app = await CreateAutoMappedAppAsync(enabled: true);
        using var client = app.GetTestClient();

        var status = await client.GetAsync("/vapecache/status");
        var stats = await client.GetAsync("/vapecache/stats");
        using var streamRequest = new HttpRequestMessage(HttpMethod.Get, "/vapecache/stream");
        using var stream = await client.SendAsync(streamRequest, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal(HttpStatusCode.OK, stats.StatusCode);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        Assert.Equal("text/event-stream", stream.Content.Headers.ContentType?.MediaType);

        await using var streamBody = await stream.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(streamBody);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        string? dataLine = null;
        while (!cts.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line is null)
                break;

            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            dataLine = line["data:".Length..].Trim();
            if (dataLine.Length > 0)
                break;
        }

        Assert.NotNull(dataLine);
        using var doc = JsonDocument.Parse(dataLine!);
        Assert.True(doc.RootElement.TryGetProperty("Lanes", out var lanesProperty));
        Assert.Equal(JsonValueKind.Array, lanesProperty.ValueKind);
    }

    [Fact]
    public async Task WithAutoMappedEndpoints_CanDisableRouteSurface()
    {
        await using var app = await CreateAutoMappedAppAsync(enabled: false);
        using var client = app.GetTestClient();

        var status = await client.GetAsync("/vapecache/status");
        Assert.Equal(HttpStatusCode.NotFound, status.StatusCode);
    }

    private static async Task<WebApplication> CreateAppAsync(bool includeBreakerControlEndpoints, string prefix = "/vapecache", bool includeIntentEndpoints = true)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development"
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddVapecacheRedisConnections();
        builder.Services.AddVapecacheCaching();

        var app = builder.Build();
        app.MapPost("/seed-intent", (ICacheIntentRegistry intentRegistry) =>
        {
            var options = new CacheEntryOptions(
                TimeSpan.FromMinutes(1),
                new CacheIntent(CacheIntentKind.ReadThrough, "endpoint-test", "tests"));
            intentRegistry.RecordSet("intent-endpoint-key", "memory", options, payloadBytes: 5);
            return Results.Ok();
        });
        app.MapVapeCacheEndpoints(prefix, includeBreakerControlEndpoints, includeLiveStreamEndpoint: false, includeIntentEndpoints: includeIntentEndpoints);
        await app.StartAsync();
        return app;
    }

    private static async Task<WebApplication> CreateAutoMappedAppAsync(bool enabled)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development"
        });
        builder.WebHost.UseTestServer();
        builder.AddVapeCache()
            .WithAutoMappedEndpoints(options =>
            {
                options.Enabled = enabled;
                options.Prefix = "/vapecache";
                options.IncludeBreakerControlEndpoints = false;
                options.LiveSampleInterval = TimeSpan.FromMilliseconds(50);
            });

        var app = builder.Build();
        await app.StartAsync();
        return app;
    }
}
