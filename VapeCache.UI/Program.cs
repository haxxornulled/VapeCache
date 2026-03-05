using VapeCache.UI.Components;
using VapeCache.UI.Features.CacheWorkbench;
using VapeCache.Extensions.Aspire;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.AddVapeCache()
    .WithRedisFromAspire("redis")
    .WithHealthChecks()
    .WithAspireTelemetry()
    .WithAutoMappedEndpoints(options =>
    {
        options.Enabled = true;
        options.Prefix = "/vapecache";
        options.IncludeBreakerControlEndpoints = false;
        options.IncludeIntentEndpoints = true;
        options.EnableLiveStream = true;
        options.EnableDashboard = true;
        options.LiveSampleInterval = TimeSpan.FromMilliseconds(500);
    });

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddScoped<CacheWorkbenchOrchestrator>();

var app = builder.Build();

app.MapDefaultEndpoints();

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
