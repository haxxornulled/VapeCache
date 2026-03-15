using Aspire.Hosting.ApplicationModel;
using System.Net.Http;

var builder = DistributedApplication.CreateBuilder(args);

var sharedRedisConnectionString = Environment.GetEnvironmentVariable("VAPECACHE_REDIS_CONNECTIONSTRING");
if (!string.IsNullOrWhiteSpace(sharedRedisConnectionString))
{
    builder.Configuration["ConnectionStrings:redis"] = sharedRedisConnectionString;
}

var useContainerRedis =
    bool.TryParse(Environment.GetEnvironmentVariable("VAPECACHE_USE_CONTAINER_REDIS"), out var parsedUseContainerRedis) &&
    parsedUseContainerRedis;
var includeConsoleLoad =
    !bool.TryParse(Environment.GetEnvironmentVariable("VAPECACHE_INCLUDE_CONSOLE_LOAD"), out var parsedIncludeConsoleLoad) ||
    parsedIncludeConsoleLoad;

if (useContainerRedis)
{
    var redis = builder.AddRedis("redis");

    var ui = builder.AddProject<Projects.VapeCache_UI>("vapecache-ui")
        .WithReference(redis)
        .WaitFor(redis)
        .WithExternalHttpEndpoints();
    ConfigureVapeCacheUiExperience(ui);

    if (includeConsoleLoad)
    {
        builder.AddProject<Projects.VapeCache_Console>("vapecache-load")
            .WithReference(redis)
            .WaitFor(redis);
    }
}
else
{
    var redis = builder.AddConnectionString("redis");

    var ui = builder.AddProject<Projects.VapeCache_UI>("vapecache-ui")
        .WithReference(redis)
        .WithExternalHttpEndpoints();
    ConfigureVapeCacheUiExperience(ui);

    if (includeConsoleLoad)
    {
        builder.AddProject<Projects.VapeCache_Console>("vapecache-load")
            .WithReference(redis);
    }
}

builder.Build().Run();

static void ConfigureVapeCacheUiExperience(IResourceBuilder<ProjectResource> ui)
{
    _ = ui
        .WithIconName("Pulse")
        .WithUrlForEndpoint("https", url =>
        {
            url.DisplayText = "VapeCache UI";
            url.DisplayOrder = 300;
            url.DisplayLocation = UrlDisplayLocation.SummaryAndDetails;
        })
        .WithUrlForEndpoint("https", _ => new ResourceUrlAnnotation
        {
            Url = "/vapecache",
            DisplayText = "VapeCache Admin",
            DisplayOrder = 290,
            DisplayLocation = UrlDisplayLocation.SummaryAndDetails
        })
        .WithUrlForEndpoint("https", _ => new ResourceUrlAnnotation
        {
            Url = "/vapecache/stats",
            DisplayText = "Admin Stats",
            DisplayOrder = 285,
            DisplayLocation = UrlDisplayLocation.DetailsOnly
        })
        .WithUrlForEndpoint("https", _ => new ResourceUrlAnnotation
        {
            Url = "/vapecache/health",
            DisplayText = "Admin Health",
            DisplayOrder = 284,
            DisplayLocation = UrlDisplayLocation.DetailsOnly
        })
        .WithUrlForEndpoint("https", _ => new ResourceUrlAnnotation
        {
            Url = "/vapecache/invalidation",
            DisplayText = "Admin Invalidation",
            DisplayOrder = 283,
            DisplayLocation = UrlDisplayLocation.DetailsOnly
        })
        .WithUrlForEndpoint("https", _ => new ResourceUrlAnnotation
        {
            Url = "/vapecache/autoscaler",
            DisplayText = "Admin Autoscaler",
            DisplayOrder = 282,
            DisplayLocation = UrlDisplayLocation.DetailsOnly
        })
        .WithUrlForEndpoint("https", _ => new ResourceUrlAnnotation
        {
            Url = "/vapecache/spill",
            DisplayText = "Admin Spill",
            DisplayOrder = 281,
            DisplayLocation = UrlDisplayLocation.DetailsOnly
        })
        .WithUrlForEndpoint("https", _ => new ResourceUrlAnnotation
        {
            Url = "/vapecache/reconciliation",
            DisplayText = "Admin Reconciliation",
            DisplayOrder = 280,
            DisplayLocation = UrlDisplayLocation.DetailsOnly
        })
        .WithUrlForEndpoint("https", _ => new ResourceUrlAnnotation
        {
            Url = "/cache-workbench",
            DisplayText = "Cache Workbench",
            DisplayOrder = 279,
            DisplayLocation = UrlDisplayLocation.SummaryAndDetails
        })
        .WithUrlForEndpoint("https", _ => new ResourceUrlAnnotation
        {
            Url = "/vapecache/api/status",
            DisplayText = "Status API",
            DisplayOrder = 270,
            DisplayLocation = UrlDisplayLocation.DetailsOnly
        })
        .WithUrlForEndpoint("https", _ => new ResourceUrlAnnotation
        {
            Url = "/vapecache/api/stats",
            DisplayText = "Stats API",
            DisplayOrder = 260,
            DisplayLocation = UrlDisplayLocation.DetailsOnly
        })
        .WithUrlForEndpoint("https", _ => new ResourceUrlAnnotation
        {
            Url = "/vapecache/api/dashboard/shared-snapshot",
            DisplayText = "Shared Snapshot",
            DisplayOrder = 250,
            DisplayLocation = UrlDisplayLocation.DetailsOnly
        })
        .WithHttpCommand(
            path: "/vapecache/api/breaker/force-open",
            displayName: "Force InMemory Fallback",
            commandName: "vapecache-breaker-force-open",
            commandOptions: new HttpCommandOptions
            {
                Method = HttpMethod.Post,
                ConfirmationMessage = "Force-open VapeCache breaker and route traffic to in-memory fallback?",
                IconName = "Warning",
                IconVariant = IconVariant.Filled,
                IsHighlighted = true
            })
        .WithHttpCommand(
            path: "/vapecache/api/breaker/clear",
            displayName: "Restore Redis Backend",
            commandName: "vapecache-breaker-clear",
            commandOptions: new HttpCommandOptions
            {
                Method = HttpMethod.Post,
                ConfirmationMessage = "Clear forced-open state and resume Redis backend traffic?",
                IconName = "ArrowCounterclockwise",
                IconVariant = IconVariant.Filled,
                IsHighlighted = true
            })
        .WithHttpCommand(
            path: "/vapecache/api/status",
            displayName: "Fetch Runtime Status",
            commandName: "vapecache-fetch-status",
            commandOptions: new HttpCommandOptions
            {
                Method = HttpMethod.Get,
                IconName = "PulseSquare",
                IconVariant = IconVariant.Regular
            })
        .WithHttpCommand(
            path: "/vapecache/api/stats",
            displayName: "Fetch Cache Stats",
            commandName: "vapecache-fetch-stats",
            commandOptions: new HttpCommandOptions
            {
                Method = HttpMethod.Get,
                IconName = "DataHistogram",
                IconVariant = IconVariant.Regular
            })
        .WithHttpCommand(
            path: "/vapecache/api/dashboard/shared-snapshot",
            displayName: "Fetch Shared Snapshot",
            commandName: "vapecache-fetch-shared-snapshot",
            commandOptions: new HttpCommandOptions
            {
                Method = HttpMethod.Get,
                IconName = "DataTrending",
                IconVariant = IconVariant.Regular
            })
        .WithHttpCommand(
            path: "/health",
            displayName: "Run Health Probe",
            commandName: "vapecache-health-probe",
            commandOptions: new HttpCommandOptions
            {
                Method = HttpMethod.Get,
                IconName = "HeartPulse",
                IconVariant = IconVariant.Regular
            })
        .WithHttpCommand(
            path: "/alive",
            displayName: "Run Liveness Probe",
            commandName: "vapecache-liveness-probe",
            commandOptions: new HttpCommandOptions
            {
                Method = HttpMethod.Get,
                IconName = "CheckmarkCircle",
                IconVariant = IconVariant.Regular
            });
}
