# VapeCache.Extensions.AspNetCore

ASP.NET Core pipeline hooks for MVC, Minimal APIs, and Blazor output caching backed by VapeCache.

## Install

```bash
dotnet add package VapeCache.Extensions.AspNetCore
```

## Setup

```csharp
using Microsoft.AspNetCore.OutputCaching;
using VapeCache.Extensions.AspNetCore;

builder.Services.AddVapecacheRedisConnections();
builder.Services.AddVapecacheCaching();

builder.Services.AddVapeCacheOutputCaching(options =>
{
    options.AddBasePolicy(policy => policy.Expire(TimeSpan.FromSeconds(30)));
}, store =>
{
    store.KeyPrefix = "vapecache:output";
    store.DefaultTtl = TimeSpan.FromSeconds(30);
});
```

```csharp
var app = builder.Build();
app.UseVapeCacheOutputCaching();

app.MapGet("/products/{id:int}", async (int id) => $"product:{id}")
    .CacheWithVapeCache();
```

For MVC/Blazor endpoints, use ASP.NET Core output cache policies/attributes as usual.  
This package replaces the default output-cache store with `VapeCacheOutputCacheStore`.

## Sticky Failover Hints (Cluster/Web-Garden)

When Redis is down, in-memory fallback is local to each node. Emit affinity hints so upstream routing can keep sessions sticky:

```csharp
builder.Services.AddVapeCacheFailoverAffinityHints(options =>
{
    options.NodeId = Environment.MachineName;
    options.CookieName = "VapeCacheAffinity";
});

var app = builder.Build();
app.UseVapeCacheFailoverAffinityHints();
```

Headers/cookie emitted:
- `X-VapeCache-Node`
- `X-VapeCache-Failover-State`
- `VapeCacheAffinity` cookie (configurable)
