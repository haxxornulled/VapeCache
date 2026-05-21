using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Abstractions.Diagnostics;
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
        Assert.Equal(BackendType.Redis, status!.CurrentBackend);
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
    public async Task MapVapeCacheAdminEndpoints_MapsBreakerControl_OnDedicatedPrefix()
    {
        await using var app = await CreateAdminOnlyAppAsync(prefix: "/internal/vapecache-admin");
        using var client = app.GetTestClient();

        var open = await client.PostAsJsonAsync(
            "/internal/vapecache-admin/breaker/force-open",
            new VapeCacheForceOpenRequest("admin-surface-test"));
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);

        var clear = await client.PostAsync("/internal/vapecache-admin/breaker/clear", content: null);
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);

        var status = await client.GetAsync("/internal/vapecache-admin/status");
        Assert.Equal(HttpStatusCode.NotFound, status.StatusCode);
    }

    [Fact]
    public async Task MapVapeCacheAdminEndpoints_CanRequireAuthorization_WithPolicy()
    {
        const string policy = "VapeCacheAdmin";
        await using var app = await CreateAdminOnlyAppAsync(
            prefix: "/internal/vapecache-admin",
            requireAuthorization: true,
            authorizationPolicy: policy);

        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(static e => e.RoutePattern.RawText is not null)
            .ToArray();

        var controlEndpoints = endpoints
            .Where(static e => e.RoutePattern.RawText!.StartsWith("/internal/vapecache-admin/breaker/", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(controlEndpoints);
        foreach (var endpoint in controlEndpoints)
        {
            var metadata = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
            Assert.Contains(metadata, x => string.Equals(x.Policy, policy, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task MapVapeCacheAdminEndpoints_MapsReconciliationControl_OnDedicatedPrefix()
    {
        await using var app = await CreateAdminOnlyAppAsync(prefix: "/internal/vapecache-admin");
        using var client = app.GetTestClient();

        var statusResponse = await client.GetAsync("/internal/vapecache-admin/reconciliation/status");
        var runResponse = await client.PostAsync("/internal/vapecache-admin/reconciliation/run", content: null);
        var flushResponse = await client.PostAsync("/internal/vapecache-admin/reconciliation/flush", content: null);

        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, runResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, flushResponse.StatusCode);

        var status = await statusResponse.Content.ReadFromJsonAsync<VapeCacheReconciliationControlResponse>();
        var run = await runResponse.Content.ReadFromJsonAsync<VapeCacheReconciliationControlResponse>();
        var flush = await flushResponse.Content.ReadFromJsonAsync<VapeCacheReconciliationControlResponse>();

        Assert.NotNull(status);
        Assert.NotNull(run);
        Assert.NotNull(flush);
        Assert.Equal("status", status!.Operation);
        Assert.Equal("reconcile", run!.Operation);
        Assert.Equal("flush", flush!.Operation);
    }

    [Fact]
    public async Task MapVapeCacheEndpoints_MapsDashboardAssets_WhenEnabled()
    {
        await using var app = await CreateAppAsync(includeBreakerControlEndpoints: false, includeDashboardEndpoint: true);
        using var client = app.GetTestClient();

        var dashboard = await client.GetAsync("/vapecache/dashboard");
        var script = await client.GetAsync("/vapecache/dashboard/dashboard.js");
        var style = await client.GetAsync("/vapecache/dashboard/dashboard.css");

        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Equal(HttpStatusCode.OK, style.StatusCode);
        Assert.Equal("text/html", dashboard.Content.Headers.ContentType?.MediaType);
        Assert.Equal("text/javascript", script.Content.Headers.ContentType?.MediaType);
        Assert.Equal("text/css", style.Content.Headers.ContentType?.MediaType);

        var dashboardHtml = await dashboard.Content.ReadAsStringAsync();
        Assert.Contains("./dashboard.js", dashboardHtml, StringComparison.Ordinal);
        Assert.Contains("./dashboard.css", dashboardHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MapVapeCacheEndpoints_ExposesSharedSnapshot_WhenPresent()
    {
        var snapshot = new VapeCacheSharedDashboardSnapshot(
            TimestampUtc: DateTimeOffset.UtcNow,
            Backend: BackendType.Redis,
            HitRate: 0.82d,
            Reads: 140,
            Writes: 32,
            Hits: 115,
            Misses: 25,
            FallbackToMemory: 0,
            RedisBreakerOpened: 0,
            StampedeKeyRejected: 0,
            StampedeLockWaitTimeout: 0,
            StampedeFailureBackoffRejected: 0,
            BreakerEnabled: true,
            BreakerOpen: false,
            BreakerConsecutiveFailures: 0,
            BreakerOpenRemaining: null,
            BreakerForcedOpen: false,
            BreakerReason: null,
            Autoscaler: null,
            Lanes: Array.Empty<RedisMuxLaneSnapshot>(),
            Spill: null,
            OriginStats: default);

        var payload = JsonSerializer.SerializeToUtf8Bytes(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var redis = new Mock<IRedisCommandExecutor>();
        redis.Setup(x => x.GetAsync(VapeCacheSharedDashboardSnapshotStore.RedisKey, It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken _) => ValueTask.FromResult<byte[]?>(payload));

        await using var app = await CreateAppAsync(
            includeBreakerControlEndpoints: false,
            redisCommandExecutor: redis.Object);
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/vapecache/dashboard/shared-snapshot");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var envelope = await response.Content.ReadFromJsonAsync<VapeCacheSharedDashboardSnapshotEnvelope>();
        Assert.NotNull(envelope);
        Assert.True(envelope!.Exists);
        Assert.True(envelope.IsFresh);
        Assert.NotNull(envelope.Snapshot);
        Assert.Equal(BackendType.Redis, envelope.Snapshot!.Backend);
        Assert.Equal(140, envelope.Snapshot.Reads);
        Assert.Equal(0, envelope.Snapshot.OriginStats.InteropReads);
    }

    [Fact]
    public async Task MapVapeCacheEndpoints_ExposesSharedSnapshot_WithOriginBreakdown()
    {
        var snapshot = new VapeCacheSharedDashboardSnapshot(
            TimestampUtc: DateTimeOffset.UtcNow,
            Backend: BackendType.Redis,
            HitRate: 0.91d,
            Reads: 220,
            Writes: 45,
            Hits: 200,
            Misses: 20,
            FallbackToMemory: 3,
            RedisBreakerOpened: 1,
            StampedeKeyRejected: 0,
            StampedeLockWaitTimeout: 0,
            StampedeFailureBackoffRejected: 0,
            BreakerEnabled: true,
            BreakerOpen: false,
            BreakerConsecutiveFailures: 0,
            BreakerOpenRemaining: null,
            BreakerForcedOpen: false,
            BreakerReason: null,
            Autoscaler: null,
            Lanes: Array.Empty<RedisMuxLaneSnapshot>(),
            Spill: null,
            OriginStats: new CacheOriginStatsSnapshot(
                NativeReads: 120,
                NativeWrites: 30,
                NativeHits: 110,
                NativeMisses: 10,
                InteropReads: 100,
                InteropWrites: 15,
                InteropHits: 90,
                InteropMisses: 10));

        var payload = JsonSerializer.SerializeToUtf8Bytes(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var redis = new Mock<IRedisCommandExecutor>();
        redis.Setup(x => x.GetAsync(VapeCacheSharedDashboardSnapshotStore.RedisKey, It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken _) => ValueTask.FromResult<byte[]?>(payload));

        await using var app = await CreateAppAsync(
            includeBreakerControlEndpoints: false,
            redisCommandExecutor: redis.Object);
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/vapecache/dashboard/shared-snapshot");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var envelope = await response.Content.ReadFromJsonAsync<VapeCacheSharedDashboardSnapshotEnvelope>();
        Assert.NotNull(envelope);
        Assert.NotNull(envelope!.Snapshot);
        Assert.Equal(120, envelope.Snapshot!.OriginStats.NativeReads);
        Assert.Equal(30, envelope.Snapshot.OriginStats.NativeWrites);
        Assert.Equal(100, envelope.Snapshot.OriginStats.InteropReads);
        Assert.Equal(15, envelope.Snapshot.OriginStats.InteropWrites);
    }

    [Fact]
    public async Task MapVapeCacheEndpoints_ExposesSharedSnapshot_NotFound_WhenMissing()
    {
        var redis = new Mock<IRedisCommandExecutor>();
        redis.Setup(x => x.GetAsync(VapeCacheSharedDashboardSnapshotStore.RedisKey, It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken _) => ValueTask.FromResult<byte[]?>(null));

        await using var app = await CreateAppAsync(
            includeBreakerControlEndpoints: false,
            redisCommandExecutor: redis.Object);
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/vapecache/dashboard/shared-snapshot");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var envelope = await response.Content.ReadFromJsonAsync<VapeCacheSharedDashboardSnapshotEnvelope>();
        Assert.NotNull(envelope);
        Assert.False(envelope!.Exists);
        Assert.Null(envelope.Snapshot);
    }

    [Fact]
    public async Task WithAutoMappedEndpoints_MapsEndpoints_WithoutProgramRouteSetup()
    {
        await using var app = await CreateAutoMappedAppAsync(enabled: true);
        using var client = app.GetTestClient();

        var status = await client.GetAsync("/vapecache/status");
        var stats = await client.GetAsync("/vapecache/stats");
        var dashboard = await client.GetAsync("/vapecache/dashboard");
        using var streamRequest = new HttpRequestMessage(HttpMethod.Get, "/vapecache/stream");
        using var stream = await client.SendAsync(streamRequest, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal(HttpStatusCode.OK, stats.StatusCode);
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
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
    public async Task WithAutoMappedEndpoints_PublishesSharedSnapshot_WhenEnabled()
    {
        var writes = new List<(string Key, byte[] Payload, TimeSpan? Ttl)>();
        var redis = new Mock<IRedisCommandExecutor>();
        redis.Setup(x => x.SetAsync(
                It.IsAny<string>(),
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()))
            .Returns((string key, ReadOnlyMemory<byte> payload, TimeSpan? ttl, CancellationToken _) =>
            {
                writes.Add((key, payload.ToArray(), ttl));
                return ValueTask.FromResult(true);
            });

        await using var app = await CreateAutoMappedAppAsync(
            enabled: false,
            redisCommandExecutor: redis.Object,
            publishSharedSnapshot: true);

        await WaitForAsync(
            () => writes.Count > 0,
            timeout: TimeSpan.FromSeconds(5));

        Assert.NotEmpty(writes);
        var write = writes[^1];
        Assert.Equal(VapeCacheSharedDashboardSnapshotStore.RedisKey, write.Key);
        Assert.Equal(VapeCacheSharedDashboardSnapshotStore.TimeToLive, write.Ttl);

        var snapshot = JsonSerializer.Deserialize<VapeCacheSharedDashboardSnapshot>(
            write.Payload,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(snapshot);
        Assert.Equal(BackendType.Redis, snapshot!.Backend);
    }

    [Fact]
    public async Task WithAutoMappedEndpoints_CanDisableRouteSurface()
    {
        await using var app = await CreateAutoMappedAppAsync(enabled: false);
        using var client = app.GetTestClient();

        var status = await client.GetAsync("/vapecache/status");
        Assert.Equal(HttpStatusCode.NotFound, status.StatusCode);
    }

    [Fact]
    public async Task WithAutoMappedEndpoints_MapsBreakerControl_OnDedicatedAdminPrefix()
    {
        await using var app = await CreateAutoMappedAppAsync(
            enabled: true,
            includeBreakerControlEndpoints: true,
            prefix: "/vapecache",
            adminPrefix: "/internal/vapecache-admin");
        using var client = app.GetTestClient();

        var legacyOpen = await client.PostAsJsonAsync(
            "/vapecache/breaker/force-open",
            new VapeCacheForceOpenRequest("legacy"));
        Assert.Equal(HttpStatusCode.NotFound, legacyOpen.StatusCode);

        var adminOpen = await client.PostAsJsonAsync(
            "/internal/vapecache-admin/breaker/force-open",
            new VapeCacheForceOpenRequest("admin"));
        Assert.Equal(HttpStatusCode.OK, adminOpen.StatusCode);

        var adminClear = await client.PostAsync("/internal/vapecache-admin/breaker/clear", content: null);
        Assert.Equal(HttpStatusCode.OK, adminClear.StatusCode);
    }

    [Fact]
    public async Task WithAutoMappedEndpoints_CanRequireAuthorization_OnAdminControlPrefix()
    {
        const string policy = "VapeCacheAdmin";
        await using var app = await CreateAutoMappedAppAsync(
            enabled: true,
            includeBreakerControlEndpoints: true,
            prefix: "/vapecache",
            adminPrefix: "/internal/vapecache-admin",
            requireAuthorizationOnAdminEndpoints: true,
            adminAuthorizationPolicy: policy);

        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(static e => e.RoutePattern.RawText is not null)
            .ToArray();

        var controlEndpoints = endpoints
            .Where(static e => e.RoutePattern.RawText!.StartsWith("/internal/vapecache-admin/breaker/", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(controlEndpoints);
        foreach (var endpoint in controlEndpoints)
        {
            var metadata = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
            Assert.Contains(metadata, x => string.Equals(x.Policy, policy, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ProgramStyleMapping_ExposesBreakerControl_OnAdminPrefixOnly()
    {
        await using var app = await CreateProgramStyleAppAsync(
            includeIntentEndpoints: false,
            includeLiveStreamEndpoint: false,
            enableBreakerControlEndpoints: true);
        using var client = app.GetTestClient();

        var publicOpen = await client.PostAsJsonAsync(
            "/vapecache/api/breaker/force-open",
            new VapeCacheForceOpenRequest("public"));
        Assert.Equal(HttpStatusCode.NotFound, publicOpen.StatusCode);

        var adminOpen = await client.PostAsJsonAsync(
            "/vapecache/admin/breaker/force-open",
            new VapeCacheForceOpenRequest("admin"));
        Assert.Equal(HttpStatusCode.OK, adminOpen.StatusCode);
    }

    [Fact]
    public async Task ProgramStyleMapping_GatesIntentAndStream_WhenDisabled()
    {
        await using var app = await CreateProgramStyleAppAsync(
            includeIntentEndpoints: false,
            includeLiveStreamEndpoint: false,
            enableBreakerControlEndpoints: false);
        using var client = app.GetTestClient();

        var intentByKey = await client.GetAsync("/vapecache/api/intent/some-key");
        var intentRecent = await client.GetAsync("/vapecache/api/intent");
        var liveStream = await client.GetAsync("/vapecache/api/stream");

        Assert.Equal(HttpStatusCode.NotFound, intentByKey.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, intentRecent.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, liveStream.StatusCode);
    }

    private static async Task<WebApplication> CreateAppAsync(
        bool includeBreakerControlEndpoints,
        string prefix = "/vapecache",
        bool includeIntentEndpoints = true,
        bool includeDashboardEndpoint = false,
        IRedisCommandExecutor? redisCommandExecutor = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development"
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RedisConnection:Host"] = "redis.internal"
        });
        builder.Services.AddVapeCacheRedisConnections();
        builder.Services.AddVapeCacheCaching();
        builder.Services.AddOptions<RedisConnectionOptions>()
            .Bind(builder.Configuration.GetSection("RedisConnection"));
        if (redisCommandExecutor is not null)
        {
            builder.Services.RemoveAll<IRedisCommandExecutor>();
            builder.Services.AddSingleton(redisCommandExecutor);
        }

        var app = builder.Build();
        app.MapPost("/seed-intent", (ICacheIntentRegistry intentRegistry) =>
        {
            var options = new CacheEntryOptions(
                TimeSpan.FromMinutes(1),
                new CacheIntent(CacheIntentKind.ReadThrough, "endpoint-test", "tests"));
            intentRegistry.RecordSet("intent-endpoint-key", BackendType.InMemory, options, payloadBytes: 5);
            return Results.Ok();
        });
        app.MapVapeCacheEndpoints(
            prefix,
            includeBreakerControlEndpoints,
            includeLiveStreamEndpoint: false,
            includeIntentEndpoints: includeIntentEndpoints,
            includeDashboardEndpoint: includeDashboardEndpoint);
        await app.StartAsync();
        return app;
    }

    private static async Task<WebApplication> CreateAutoMappedAppAsync(
        bool enabled,
        bool includeBreakerControlEndpoints = false,
        string prefix = "/vapecache",
        string adminPrefix = "/vapecache/admin",
        bool requireAuthorizationOnAdminEndpoints = false,
        string? adminAuthorizationPolicy = null,
        IRedisCommandExecutor? redisCommandExecutor = null,
        bool publishSharedSnapshot = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development"
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RedisConnection:Host"] = "redis.internal"
        });
        builder.AddVapeCache()
            .WithAutoMappedEndpoints(options =>
            {
                options.Enabled = enabled;
                options.Prefix = prefix;
                options.AdminPrefix = adminPrefix;
                options.IncludeBreakerControlEndpoints = includeBreakerControlEndpoints;
                options.RequireAuthorizationOnAdminEndpoints = requireAuthorizationOnAdminEndpoints;
                options.AdminAuthorizationPolicy = adminAuthorizationPolicy;
                options.IncludeIntentEndpoints = true;
                options.EnableLiveStream = true;
                options.EnableDashboard = true;
                options.LiveSampleInterval = TimeSpan.FromMilliseconds(50);
                options.PublishSharedSnapshot = publishSharedSnapshot;
                options.SharedSnapshotPublishInterval = TimeSpan.FromMilliseconds(50);
            });
        if (redisCommandExecutor is not null)
        {
            builder.Services.RemoveAll<IRedisCommandExecutor>();
            builder.Services.AddSingleton(redisCommandExecutor);
        }
        builder.Services.AddOptions<RedisConnectionOptions>()
            .Bind(builder.Configuration.GetSection("RedisConnection"));

        var app = builder.Build();
        await app.StartAsync();
        return app;
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < timeout)
        {
            if (condition())
                return;

            await Task.Delay(50);
        }

        Assert.True(condition(), "Timed out waiting for condition.");
    }

    private static async Task<WebApplication> CreateAdminOnlyAppAsync(
        string prefix,
        bool requireAuthorization = false,
        string? authorizationPolicy = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development"
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RedisConnection:Host"] = "redis.internal"
        });
        builder.Services.AddVapeCacheRedisConnections();
        builder.Services.AddVapeCacheCaching();
        builder.Services.AddOptions<RedisConnectionOptions>()
            .Bind(builder.Configuration.GetSection("RedisConnection"));

        var app = builder.Build();
        app.MapVapeCacheAdminEndpoints(prefix, requireAuthorization, authorizationPolicy);
        await app.StartAsync();
        return app;
    }

    private static async Task<WebApplication> CreateProgramStyleAppAsync(
        bool includeIntentEndpoints,
        bool includeLiveStreamEndpoint,
        bool enableBreakerControlEndpoints)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development"
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RedisConnection:Host"] = "redis.internal"
        });
        builder.Services.AddVapeCacheRedisConnections();
        builder.Services.AddVapeCacheCaching();
        builder.Services.AddOptions<RedisConnectionOptions>()
            .Bind(builder.Configuration.GetSection("RedisConnection"));

        var app = builder.Build();
        app.MapVapeCacheEndpoints(
            prefix: "/vapecache/api",
            includeBreakerControlEndpoints: false,
            includeLiveStreamEndpoint: includeLiveStreamEndpoint,
            includeIntentEndpoints: includeIntentEndpoints,
            includeDashboardEndpoint: false);

        if (enableBreakerControlEndpoints)
            app.MapVapeCacheAdminEndpoints(prefix: "/vapecache/admin", requireAuthorization: false, authorizationPolicy: null);

        await app.StartAsync();
        return app;
    }
}
