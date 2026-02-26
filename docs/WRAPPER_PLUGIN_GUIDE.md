# Wrapper and Plugin Guide

This guide shows how to build wrapper-facing APIs around VapeCache without bloating the core library.

## Design Rules

- Keep `VapeCache` core transport-agnostic.
- Put HTTP endpoint shape in extension packages.
- Put host-specific behavior behind plugin interfaces.
- Use `IOptionsMonitor<T>` for runtime tuning where practical.

## 1) Map Wrapper Endpoints

`VapeCache.Extensions.Aspire` now provides endpoint mapping helpers:

```csharp
builder.AddVapeCache()
    .WithHealthChecks()
    .WithAspireTelemetry(options => options.UseSeq("http://localhost:5341"))
    .WithAutoMappedEndpoints(options =>
    {
        options.Prefix = "/vapecache";
        options.IncludeBreakerControlEndpoints = false;
    });

var app = builder.Build();
app.MapHealthChecks("/health");
```

Mapped routes:

- `GET /vapecache/status`
- `GET /vapecache/stats`
- `GET /vapecache/stream` (SSE realtime channel, `event: vapecache-stats`)
- `POST /vapecache/breaker/force-open` (optional when enabled)
- `POST /vapecache/breaker/clear` (optional when enabled)

`/status` and `/stats` include stampede protection counters:
- `stampedeKeyRejected`
- `stampedeLockWaitTimeout`
- `stampedeFailureBackoffRejected`

## 2) Build Plugins in the Console Host

Reference implementation:

- `VapeCache.Console/Plugins/IVapeCachePlugin.cs`
- `VapeCache.Console/Plugins/PluginDemoHostedService.cs`
- `VapeCache.Console/Plugins/SampleCatalogPlugin.cs`

Minimal plugin:

```csharp
public sealed class InventoryPlugin : IVapeCachePlugin
{
    public string Name => "inventory";

    public async ValueTask ExecuteAsync(
        ICacheService cache,
        ICurrentCacheService current,
        CancellationToken cancellationToken)
    {
        await cache.SetAsync(
            "plugin:inventory:heartbeat",
            "ok"u8.ToArray(),
            new CacheEntryOptions(TimeSpan.FromMinutes(5)),
            cancellationToken);
    }
}
```

Registration:

```csharp
services.AddSingleton<IVapeCachePlugin, InventoryPlugin>();
services.AddHostedService<PluginDemoHostedService>();
```

## 3) GroceryStore Dogfood Run

Use the built-in dogfood runner:

```powershell
powershell -ExecutionPolicy Bypass -File VapeCache.Console/run-grocery-dogfood.ps1 `
  -ConnectionString "redis://localhost:6379/0" `
  -ConcurrentShoppers 200 `
  -TotalShoppers 5000 `
  -TargetDurationSeconds 30 `
  -EnablePluginDemo
```

This runs the same typed collection APIs (`LIST`, `SET`, `HASH`, simple cache) that production wrappers rely on.

## 4) Security Notes

- Keep breaker-control routes behind authN/authZ.
- Use read-only status routes for public diagnostics.
- Do not expose force-open/clear routes without explicit protection.

## 5) Related Docs

- `VapeCache.Extensions.Aspire/README.md`
- `docs/GROCERY_STORE_DEMO.md`
- `docs/BENCHMARKING.md`
- `VapeCache.Console/PLUGINS.md`
