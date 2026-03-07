using Autofac;
using Autofac.Extensions.DependencyInjection;
using VapeCache.UI.Components;
using VapeCache.UI.Features.CacheWorkbench;
using VapeCache.UI.Features.Dashboard;
using VapeCache.Extensions.Aspire;

var builder = WebApplication.CreateBuilder(args);
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
    // AddServiceDefaults already wires UseOtlpExporter; keep VapeCache meter/source registration only.
    .WithAspireTelemetry(options => options.DisableOtlpExporter())
    .WithAutoMappedEndpoints(options =>
    {
        options.Enabled = false;
        options.LiveSampleInterval = TimeSpan.FromMilliseconds(500);
        options.LiveChannelCapacity = 512;
    });

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddScoped<CacheWorkbenchOrchestrator>();
builder.Services.AddScoped<VapeCacheDashboardOrchestrator>();

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapVapeCacheEndpoints(
    prefix: "/vapecache",
    includeBreakerControlEndpoints: true,
    includeLiveStreamEndpoint: true,
    includeIntentEndpoints: true,
    includeDashboardEndpoint: false);

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
