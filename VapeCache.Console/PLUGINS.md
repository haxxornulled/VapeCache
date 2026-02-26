# VapeCache Console Plugin Example

This console host ships with a minimal plugin contract so wrapper developers can extend behavior without modifying core cache internals.

## Files

- `VapeCache.Console/Plugins/IVapeCachePlugin.cs`
- `VapeCache.Console/Plugins/PluginDemoHostedService.cs`
- `VapeCache.Console/Plugins/SampleCatalogPlugin.cs`
- `VapeCache.Console/Plugins/PluginDemoOptions.cs`

## How It Works

1. `PluginDemoHostedService` resolves all `IVapeCachePlugin` registrations.
2. When `PluginDemo:Enabled=true`, each plugin executes once on startup.
3. Plugins receive `ICacheService` + `ICurrentCacheService` so they use the same runtime path as production code.

## Register Your Plugin

```csharp
services.AddSingleton<IVapeCachePlugin, MyPlugin>();
services.AddHostedService<PluginDemoHostedService>();
```

## Minimal Plugin Example

```csharp
public sealed class MyPlugin : IVapeCachePlugin
{
    public string Name => "my-plugin";

    public async ValueTask ExecuteAsync(
        ICacheService cache,
        ICurrentCacheService current,
        CancellationToken cancellationToken)
    {
        await cache.SetAsync(
            "plugin:my-plugin:ping",
            "ok"u8.ToArray(),
            new CacheEntryOptions(TimeSpan.FromMinutes(5)),
            cancellationToken);
    }
}
```

## Config

```json
"PluginDemo": {
  "Enabled": false,
  "KeyPrefix": "plugin:sample",
  "Ttl": "00:05:00"
}
```

Enable this only in environments where you want plugin startup execution.
