using VapeCache.BlazorDemo.Client.Pages;
using VapeCache.BlazorDemo.Components;
using VapeCache.Infrastructure.DependencyInjection;
using VapeCache.Persistence;
using VapeCache.Reconciliation;

var builder = WebApplication.CreateBuilder(args);

// ==============================================
// VapeCache Configuration
// ==============================================

// Generate a test license key for demo purposes
var licenseKey = Environment.GetEnvironmentVariable("VAPECACHE_LICENSE_KEY")
    ?? "VCENT-demo-1767033600-8B9C7A5E4D3F2A1B";  // Demo key (expires 2025-12-30)

// Add VapeCache with Autofac DI
builder.Services.AddVapeCache(autofacBuilder =>
{
    // Configure Redis connection
    autofacBuilder.ConfigureRedis(redis =>
    {
        redis.EndpointConfigurations.Add(new()
        {
            Host = "localhost",
            Port = 6379
        });
        redis.MaxConnectionPoolSize = 10;
    });

    // Configure circuit breaker
    autofacBuilder.ConfigureCircuitBreaker(cb =>
    {
        cb.FailureThreshold = 5;
        cb.OpenDuration = TimeSpan.FromSeconds(30);
        cb.SamplingDuration = TimeSpan.FromSeconds(60);
    });

    // Configure in-memory cache (fallback when Redis is down)
    autofacBuilder.ConfigureInMemory(mem =>
    {
        mem.SizeLimit = 100_000_000;  // 100 MB
    });
});

// Add VapeCache Persistence (Enterprise feature - spill-to-disk)
builder.Services.AddVapeCachePersistence(
    licenseKey: licenseKey,
    configure: options =>
    {
        options.BasePath = Path.Combine(builder.Environment.ContentRootPath, "Data", "Spill");
        options.MaxTotalSizeBytes = 500_000_000;  // 500 MB max spill
    });

// Add VapeCache Reconciliation (Enterprise feature - zero data loss)
builder.Services.AddVapeCacheRedisReconciliation(
    licenseKey: licenseKey,
    configure: options =>
    {
        options.Enabled = true;
        options.MaxPendingOperations = 50_000;
        options.MaxOperationsPerRun = 1_000;
    },
    configureStore: store =>
    {
        store.UseSqlite = true;
        store.DatabasePath = Path.Combine(builder.Environment.ContentRootPath, "Data", "reconciliation.db");
    });

// Add Reconciliation Reaper (automatic background reconciliation)
builder.Services.AddReconciliationReaper(reaper =>
{
    reaper.Enabled = true;
    reaper.Interval = TimeSpan.FromSeconds(30);
    reaper.InitialDelay = TimeSpan.FromSeconds(5);
});

// ==============================================
// Blazor Configuration
// ==============================================

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
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
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(VapeCache.BlazorDemo.Client._Imports).Assembly);

app.Run();
