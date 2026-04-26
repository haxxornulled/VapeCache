using Aspire.Hosting.ApplicationModel;
using System.Net.Http;

var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
{
    Args = args,
    AllowUnsecuredTransport = true
});

ApplySharedRedisConfiguration(builder);

var useContainerRedis =
    bool.TryParse(Environment.GetEnvironmentVariable("VAPECACHE_USE_CONTAINER_REDIS"), out var parsedUseContainerRedis) &&
    parsedUseContainerRedis;

if (useContainerRedis)
{
    var redis = builder.AddRedis("redis");
    var ui = builder.AddProject<Projects.VapeCache_UI>("vapecache-ui")
        .WithReference(redis)
        .WaitFor(redis);
    ConfigureVapeCacheUiExperience(ui);

    var stress = builder.AddProject<Projects.VapeCache_StressHost>("vapecache-stress")
        .WithReference(redis)
        .WaitFor(redis);
    ConfigureVapeCacheStressExperience(stress);
}
else
{
    var redis = builder.AddConnectionString("redis");
    var ui = builder.AddProject<Projects.VapeCache_UI>("vapecache-ui")
        .WithReference(redis);
    ConfigureVapeCacheUiExperience(ui);

    var stress = builder.AddProject<Projects.VapeCache_StressHost>("vapecache-stress")
        .WithReference(redis);
    ConfigureVapeCacheStressExperience(stress);
}

builder.Build().Run();

static void ApplySharedRedisConfiguration(IDistributedApplicationBuilder builder)
{
    var sharedRedisConnectionString = Environment.GetEnvironmentVariable("VAPECACHE_REDIS_CONNECTIONSTRING");
    if (string.IsNullOrWhiteSpace(sharedRedisConnectionString))
        return;

    builder.Configuration["ConnectionStrings:redis"] = sharedRedisConnectionString;
}

static void ConfigureVapeCacheUiExperience(IResourceBuilder<ProjectResource> ui)
{
    _ = ui.WithIconName("Pulse");

    foreach (var annotation in AppHostMappings.UiUrlAnnotations)
        _ = ui.WithUrlForEndpoint(AppHostMappings.HttpsEndpointName, _ => annotation.ToAnnotation());

    foreach (var command in AppHostMappings.UiCommands)
        _ = ui.WithHttpCommand(
            path: command.Path,
            displayName: command.DisplayName,
            commandName: command.CommandName,
            commandOptions: command.Options);
}

static void ConfigureVapeCacheStressExperience(IResourceBuilder<ProjectResource> stress)
{
    _ = stress.WithIconName("Pulse");

    foreach (var annotation in AppHostMappings.StressUrlAnnotations)
        _ = stress.WithUrlForEndpoint(AppHostMappings.HttpsEndpointName, _ => annotation.ToAnnotation());

    foreach (var command in AppHostMappings.StressCommands)
        _ = stress.WithHttpCommand(
            path: command.Path,
            displayName: command.DisplayName,
            commandName: command.CommandName,
            commandOptions: command.Options);
}

internal sealed record ResourceUrlMapping(
    string Url,
    string DisplayText,
    int DisplayOrder,
    UrlDisplayLocation DisplayLocation)
{
    public static ResourceUrlMapping Summary(string url, string displayText, int displayOrder)
        => new(url, displayText, displayOrder, UrlDisplayLocation.SummaryAndDetails);

    public static ResourceUrlMapping Details(string url, string displayText, int displayOrder)
        => new(url, displayText, displayOrder, UrlDisplayLocation.DetailsOnly);

    public ResourceUrlAnnotation ToAnnotation()
        => new()
        {
            Url = Url,
            DisplayText = DisplayText,
            DisplayOrder = DisplayOrder,
            DisplayLocation = DisplayLocation
        };
}

internal sealed record HttpCommandMapping(
    string Path,
    string DisplayName,
    string CommandName,
    HttpCommandOptions Options)
{
    public static HttpCommandMapping HighlightedPost(
        string path,
        string displayName,
        string commandName,
        string confirmationMessage,
        string iconName)
        => new(
            path,
            displayName,
            commandName,
            new HttpCommandOptions
            {
                Method = HttpMethod.Post,
                ConfirmationMessage = confirmationMessage,
                IconName = iconName,
                IconVariant = IconVariant.Filled,
                IsHighlighted = true
            });

    public static HttpCommandMapping FilledPost(
        string path,
        string displayName,
        string commandName,
        string confirmationMessage,
        string iconName)
        => new(
            path,
            displayName,
            commandName,
            new HttpCommandOptions
            {
                Method = HttpMethod.Post,
                ConfirmationMessage = confirmationMessage,
                IconName = iconName,
                IconVariant = IconVariant.Filled
            });

    public static HttpCommandMapping RegularGet(
        string path,
        string displayName,
        string commandName,
        string iconName)
        => new(
            path,
            displayName,
            commandName,
            new HttpCommandOptions
            {
                Method = HttpMethod.Get,
                IconName = iconName,
                IconVariant = IconVariant.Regular
            });
}

internal static class AppHostMappings
{
    public const string HttpsEndpointName = "https";

    public static readonly ResourceUrlMapping[] UiUrlAnnotations =
    [
        ResourceUrlMapping.Summary("", "VapeCache UI", 300),
        ResourceUrlMapping.Summary("/vapecache", "VapeCache Admin", 290),
        ResourceUrlMapping.Details("/vapecache/stats", "Admin Stats", 285),
        ResourceUrlMapping.Details("/vapecache/health", "Admin Health", 284),
        ResourceUrlMapping.Details("/vapecache/invalidation", "Admin Invalidation", 283),
        ResourceUrlMapping.Details("/vapecache/autoscaler", "Admin Autoscaler", 282),
        ResourceUrlMapping.Details("/vapecache/spill", "Admin Spill", 281),
        ResourceUrlMapping.Details("/vapecache/reconciliation", "Admin Reconciliation", 280),
        ResourceUrlMapping.Details("/vapecache/policies", "Admin Policies", 279),
        ResourceUrlMapping.Details("/vapecache/streams", "Admin Streams", 278),
        ResourceUrlMapping.Summary("/cache-workbench", "Cache Workbench", 277),
        ResourceUrlMapping.Details("/vapecache/api/status", "Status API", 270),
        ResourceUrlMapping.Details("/vapecache/api/stats", "Stats API", 260),
        ResourceUrlMapping.Details("/vapecache/api/dashboard/shared-snapshot", "Shared Snapshot", 250)
    ];

    public static readonly HttpCommandMapping[] UiCommands =
    [
        HttpCommandMapping.HighlightedPost(
            "/vapecache/admin/breaker/force-open",
            "Force InMemory Fallback",
            "vapecache-breaker-force-open",
            "Force-open VapeCache breaker and route traffic to in-memory fallback?",
            "Warning"),
        HttpCommandMapping.HighlightedPost(
            "/vapecache/admin/breaker/clear",
            "Restore Redis Backend",
            "vapecache-breaker-clear",
            "Clear forced-open state and resume Redis backend traffic?",
            "ArrowCounterclockwise"),
        HttpCommandMapping.RegularGet(
            "/vapecache/admin/reconciliation/status",
            "Reconciliation Status",
            "vapecache-reconciliation-status",
            "DataTrending"),
        HttpCommandMapping.HighlightedPost(
            "/vapecache/admin/reconciliation/run",
            "Run Reconciliation Pass",
            "vapecache-reconciliation-run",
            "Trigger a reconciliation pass now?",
            "ArrowSyncCircle"),
        HttpCommandMapping.FilledPost(
            "/vapecache/admin/reconciliation/flush",
            "Flush Reconciliation State",
            "vapecache-reconciliation-flush",
            "Flush persisted reconciliation state?",
            "DeleteDismiss"),
        HttpCommandMapping.RegularGet(
            "/vapecache/api/status",
            "Fetch Runtime Status",
            "vapecache-fetch-status",
            "PulseSquare"),
        HttpCommandMapping.RegularGet(
            "/vapecache/api/stats",
            "Fetch Cache Stats",
            "vapecache-fetch-stats",
            "DataHistogram"),
        HttpCommandMapping.RegularGet(
            "/vapecache/api/dashboard/shared-snapshot",
            "Fetch Shared Snapshot",
            "vapecache-fetch-shared-snapshot",
            "DataTrending"),
        HttpCommandMapping.RegularGet(
            "/health",
            "Run Health Probe",
            "vapecache-health-probe",
            "HeartPulse"),
        HttpCommandMapping.RegularGet(
            "/alive",
            "Run Liveness Probe",
            "vapecache-liveness-probe",
            "CheckmarkCircle")
    ];

    public static readonly ResourceUrlMapping[] StressUrlAnnotations =
    [
        ResourceUrlMapping.Summary("", "Stress Host", 240),
        ResourceUrlMapping.Summary("/stress/api/status", "Stress Status", 239)
    ];

    public static readonly HttpCommandMapping[] StressCommands =
    [
        HttpCommandMapping.HighlightedPost(
            "/stress/api/start",
            "Start Stress Run",
            "vapecache-stress-start",
            "Start the live VapeCache stress workload?",
            "Play"),
        HttpCommandMapping.HighlightedPost(
            "/stress/api/stop",
            "Stop Stress Run",
            "vapecache-stress-stop",
            "Stop the live VapeCache stress workload?",
            "Stop"),
        HttpCommandMapping.RegularGet(
            "/stress/api/status",
            "Fetch Stress Status",
            "vapecache-stress-status",
            "PulseSquare")
    ];
}
