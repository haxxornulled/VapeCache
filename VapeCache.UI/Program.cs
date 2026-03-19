using Autofac;
using Autofac.Extensions.DependencyInjection;
using VapeCache.UI.Components;
using VapeCache.UI.Features.Admin;
using VapeCache.UI.Features.CacheWorkbench;
using VapeCache.UI.Features.Dashboard;
using VapeCache.Extensions.AdminAuth;
using VapeCache.Extensions.Aspire;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());
builder.Host.ConfigureContainer<ContainerBuilder>(static _ => { });
var allowInsecureAdminInDevelopment = !builder.Configuration.GetValue<bool>(
    "VapeCache:Endpoints:RequireAdminAuthorizationInDevelopment");
var adminAuthorizationPolicy = VapeCacheAdminPageDefaults.AuthorizationPolicyName;
var enableAdminControlEndpoints =
    builder.Configuration.GetValue<bool>("VapeCache:Endpoints:EnableBreakerControl");
var includeIntentEndpoints =
    builder.Environment.IsDevelopment()
    || builder.Configuration.GetValue<bool>("VapeCache:Endpoints:EnableIntentEndpoints");
var includeLiveStreamEndpoint =
    builder.Environment.IsDevelopment()
    || builder.Configuration.GetValue<bool>("VapeCache:Endpoints:EnableLiveStream");
var requireAuthorizationOnAdminControlEndpoints =
    !builder.Environment.IsDevelopment() || !allowInsecureAdminInDevelopment;
builder.Services.AddVapeCacheAdminAuthentication(
    configuration: builder.Configuration,
    requireAdminAuthorization: requireAuthorizationOnAdminControlEndpoints,
    authorizationPolicy: VapeCacheAdminPageDefaults.AuthorizationPolicyName,
    allowAnonymousAdminPolicy: builder.Environment.IsDevelopment() && allowInsecureAdminInDevelopment);

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
    // Optional server-side Redis view from redis_exporter; toggle via VapeCache:RedisExporter options.
    .WithRedisExporterMetrics()
    .WithAutoMappedEndpoints(options =>
    {
        options.Enabled = false;
        options.LiveSampleInterval = TimeSpan.FromMilliseconds(500);
        options.LiveChannelCapacity = 512;
    });

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddVapeCacheAdminUi();
builder.Services.AddScoped<CacheWorkbenchOrchestrator>();
builder.Services.AddScoped<VapeCacheDashboardOrchestrator>();

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapVapeCacheEndpoints(
    prefix: "/vapecache/api",
    includeBreakerControlEndpoints: false,
    includeLiveStreamEndpoint: includeLiveStreamEndpoint,
    includeIntentEndpoints: includeIntentEndpoints,
    includeDashboardEndpoint: false);
if (enableAdminControlEndpoints)
{
    app.MapVapeCacheAdminEndpoints(
        prefix: "/vapecache/admin",
        requireAuthorization: requireAuthorizationOnAdminControlEndpoints,
        authorizationPolicy: adminAuthorizationPolicy);
}

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
app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
