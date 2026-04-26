using Autofac;
using Autofac.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using VapeCache.Extensions.Aspire;
using VapeCache.Extensions.Aspire.Hosting;
using VapeCache.Extensions.DistributedCache;
using VapeCache.Extensions.Logging;
using VapeCache.StressHost;

var builder = WebApplication.CreateBuilder(args);
VapeCacheRuntimeHostDefaults.ApplyRedisMultiplexerDefaults(builder.Configuration);

var redisConnectionString = builder.Configuration["RedisConnection:ConnectionString"];
if (string.IsNullOrWhiteSpace(redisConnectionString))
{
    redisConnectionString = Environment.GetEnvironmentVariable("VAPECACHE_REDIS_CONNECTIONSTRING");
}

if (string.IsNullOrWhiteSpace(redisConnectionString))
{
    redisConnectionString = builder.Configuration.GetConnectionString("redis");
}

if (!string.IsNullOrWhiteSpace(redisConnectionString))
{
    builder.Configuration["RedisConnection:ConnectionString"] = redisConnectionString;
}

builder.Host.UseSerilog((context, services, loggerConfig) =>
{
    loggerConfig.ConfigureVapeCacheLogging(
        context.Configuration,
        services,
        context.HostingEnvironment.EnvironmentName);
});

builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());
builder.Host.ConfigureContainer<ContainerBuilder>(static _ => { });

builder.AddServiceDefaults();

builder.AddVapeCacheClientBuilder(registerCoreServices: false)
    .UseAutofacModules(options =>
    {
        options.ConnectionName = "redis";
        options.TransportMode = VapeCacheAspireTransportMode.MaxThroughput;
    })
    .WithRedisFromAspire("redis")
    .WithStartupWarmup(options =>
    {
        options.Enabled = true;
        options.ConnectionsToWarm = 8;
        options.RequiredSuccessfulConnections = 4;
        options.ValidatePing = true;
        options.Timeout = TimeSpan.FromSeconds(15);
        options.FailFastOnWarmupFailure = false;
    })
    .WithHealthChecks()
    .WithAspireTelemetry(options => options.DisableOtlpExporter())
    .WithRedisExporterMetrics()
    .WithAutoMappedEndpoints(options =>
    {
        options.Enabled = false;
        options.PublishSharedSnapshot = true;
        options.SharedSnapshotPublishInterval = TimeSpan.FromMilliseconds(250);
    });

builder.Services.AddVapeCacheDistributedCache();
builder.Services.AddOptions<RuntimeStressHostOptions>()
    .Bind(builder.Configuration.GetSection(RuntimeStressHostOptions.SectionName));
builder.Services.AddSingleton<RuntimeStressCoordinator>();
builder.Services.AddSingleton<IHostedService>(static sp => sp.GetRequiredService<RuntimeStressCoordinator>());
builder.Services.AddSingleton<IHostedLifecycleService>(static sp => sp.GetRequiredService<RuntimeStressCoordinator>());

var app = builder.Build();

app.UseSerilogRequestLogging();
app.MapDefaultEndpoints();

app.MapGet("/", (IOptionsMonitor<RuntimeStressHostOptions> options, RuntimeStressCoordinator coordinator) =>
{
    return Results.Ok(new
    {
        service = "vapecache-stress-host",
        status = coordinator.GetStatus(),
        options = options.CurrentValue
    });
});

app.MapGet("/stress/api/status", (RuntimeStressCoordinator coordinator) =>
    Results.Ok(coordinator.GetStatus()));

app.MapPost("/stress/api/start", async (RuntimeStressCoordinator coordinator, CancellationToken ct) =>
{
    var status = await coordinator.StartRunAsync(ct).ConfigureAwait(false);
    return Results.Ok(status);
});

app.MapPost("/stress/api/stop", async (RuntimeStressCoordinator coordinator, CancellationToken ct) =>
{
    var status = await coordinator.StopRunAsync(ct).ConfigureAwait(false);
    return Results.Ok(status);
});

StressHostLog.LogStarting(
    app.Logger,
    app.Environment.EnvironmentName,
    app.Configuration["RedisConnection:ConnectionString"] ?? "<not-configured>");

app.Run();
