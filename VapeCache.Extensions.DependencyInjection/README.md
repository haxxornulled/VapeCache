# VapeCache.Extensions.DependencyInjection

One-call `IServiceCollection` facade for wiring VapeCache into a .NET application.

## Install

```bash
dotnet add package VapeCache.Extensions.DependencyInjection
```

## Hybrid Redis Runtime

```csharp
using VapeCache.Extensions.DependencyInjection;

builder.Services.AddVapeCache(builder.Configuration)
    .WithCacheStampedeProfile(CacheStampedeProfile.Balanced);
```

## In-Memory-Only Runtime

```csharp
using VapeCache.Extensions.DependencyInjection;

builder.Services.AddVapeCacheInMemory(builder.Configuration)
    .WithCacheStampedeProfile(CacheStampedeProfile.Balanced);
```

Use the in-memory-only path for local dev, tests, and lightweight single-node hosts when you do not want Redis.

## Docs

- Quick start: https://github.com/haxxornulled/VapeCache/blob/main/docs/QUICKSTART.md
- Configuration: https://github.com/haxxornulled/VapeCache/blob/main/docs/CONFIGURATION.md
- API reference: https://github.com/haxxornulled/VapeCache/blob/main/docs/API_REFERENCE.md
