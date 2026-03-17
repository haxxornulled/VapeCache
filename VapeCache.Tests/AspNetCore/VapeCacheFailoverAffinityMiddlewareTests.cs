using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using VapeCache.Abstractions.Caching;
using VapeCache.Extensions.AspNetCore;

namespace VapeCache.Tests.AspNetCore;

public sealed class VapeCacheFailoverAffinityMiddlewareTests
{
    [Fact]
    public async Task UseVapeCacheFailoverAffinityHints_EmitsNodeHeadersAndCookie_WhenBreakerOpen()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development"
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IRedisCircuitBreakerState>(new StubBreakerState { EnabledValue = true, IsOpenValue = true });
        builder.Services.AddVapeCacheFailoverAffinityHints(options =>
        {
            options.NodeId = "node-a";
            options.CookieName = "vc-affinity";
        });

        var app = builder.Build();
        app.UseVapeCacheFailoverAffinityHints();
        app.MapGet("/ok", () => "ok");
        await app.StartAsync();

        using var client = app.GetTestClient();
        var response = await client.GetAsync("/ok");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("X-VapeCache-Node", out var nodeValues));
        Assert.Contains("node-a", nodeValues!);
        Assert.True(response.Headers.TryGetValues("X-VapeCache-Failover-State", out var stateValues));
        Assert.Contains("fallback-open", stateValues!);
        Assert.True(ContainsSetCookie(response, "vc-affinity=node-a"));

        await app.StopAsync();
    }

    [Fact]
    public async Task UseVapeCacheFailoverAffinityHints_EmitsMismatchHeader_WhenCookieTargetsDifferentNode()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development"
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IRedisCircuitBreakerState>(new StubBreakerState { EnabledValue = true, IsOpenValue = true });
        builder.Services.AddVapeCacheFailoverAffinityHints(options =>
        {
            options.NodeId = "node-b";
            options.CookieName = "vc-affinity";
        });

        var app = builder.Build();
        app.UseVapeCacheFailoverAffinityHints();
        app.MapGet("/ok", () => "ok");
        await app.StartAsync();

        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/ok");
        request.Headers.Add("Cookie", "vc-affinity=node-a");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("X-VapeCache-Affinity-Mismatch", out var mismatchValues));
        Assert.Contains("1", mismatchValues!);

        await app.StopAsync();
    }

    [Fact]
    public async Task UseVapeCacheFailoverAffinityHints_DoesNotEmitHints_WhenDisabled()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development"
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IRedisCircuitBreakerState>(new StubBreakerState { EnabledValue = true, IsOpenValue = true });
        builder.Services.AddVapeCacheFailoverAffinityHints(options =>
        {
            options.Enabled = false;
            options.CookieName = "vc-affinity";
        });

        var app = builder.Build();
        app.UseVapeCacheFailoverAffinityHints();
        app.MapGet("/ok", () => "ok");
        await app.StartAsync();

        using var client = app.GetTestClient();
        var response = await client.GetAsync("/ok");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("X-VapeCache-Node"));
        Assert.False(response.Headers.Contains("X-VapeCache-Failover-State"));
        Assert.DoesNotContain(response.Headers, static h => h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase));

        await app.StopAsync();
    }

    [Fact]
    public async Task UseVapeCacheFailoverAffinityHints_DoesNotSetCookie_WhenHealthyAndCookieOnlyDuringFailover()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development"
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IRedisCircuitBreakerState>(new StubBreakerState { EnabledValue = true, IsOpenValue = false });
        builder.Services.AddVapeCacheFailoverAffinityHints(options =>
        {
            options.NodeId = "node-c";
            options.CookieName = "vc-affinity";
            options.SetCookieOnlyWhenFailingOver = true;
        });

        var app = builder.Build();
        app.UseVapeCacheFailoverAffinityHints();
        app.MapGet("/ok", () => "ok");
        await app.StartAsync();

        using var client = app.GetTestClient();
        var response = await client.GetAsync("/ok");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("X-VapeCache-Failover-State", out var stateValues));
        Assert.Contains("redis-healthy", stateValues!);
        Assert.DoesNotContain(response.Headers, static h => h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase));

        await app.StopAsync();
    }

    [Fact]
    public async Task UseVapeCacheFailoverAffinityHints_SetsCookie_WhenHealthyAndAlwaysCookieMode()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development"
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IRedisCircuitBreakerState>(new StubBreakerState { EnabledValue = true, IsOpenValue = false });
        builder.Services.AddVapeCacheFailoverAffinityHints(options =>
        {
            options.NodeId = "node-d";
            options.CookieName = "vc-affinity";
            options.SetCookieOnlyWhenFailingOver = false;
        });

        var app = builder.Build();
        app.UseVapeCacheFailoverAffinityHints();
        app.MapGet("/ok", () => "ok");
        await app.StartAsync();

        using var client = app.GetTestClient();
        var response = await client.GetAsync("/ok");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(ContainsSetCookie(response, "vc-affinity=node-d"));

        await app.StopAsync();
    }

    [Fact]
    public async Task UseVapeCacheFailoverAffinityHints_DoesNotEmitMismatchHeader_WhenDisabled()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development"
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IRedisCircuitBreakerState>(new StubBreakerState { EnabledValue = true, IsOpenValue = true });
        builder.Services.AddVapeCacheFailoverAffinityHints(options =>
        {
            options.NodeId = "node-b";
            options.CookieName = "vc-affinity";
            options.EmitMismatchHeader = false;
        });

        var app = builder.Build();
        app.UseVapeCacheFailoverAffinityHints();
        app.MapGet("/ok", () => "ok");
        await app.StartAsync();

        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/ok");
        request.Headers.Add("Cookie", "vc-affinity=node-a");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("X-VapeCache-Affinity-Mismatch"));

        await app.StopAsync();
    }

    private sealed class StubBreakerState : IRedisCircuitBreakerState
    {
        public bool EnabledValue { get; set; }
        public bool IsOpenValue { get; set; }

        public bool Enabled => EnabledValue;
        public bool IsOpen => IsOpenValue;
        public int ConsecutiveFailures => 0;
        public TimeSpan? OpenRemaining => null;
        public bool HalfOpenProbeInFlight => false;
    }

    private static bool ContainsSetCookie(HttpResponseMessage response, string expectedCookieToken)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
            return false;

        return values.Any(v => v.Contains(expectedCookieToken, StringComparison.Ordinal));
    }
}
