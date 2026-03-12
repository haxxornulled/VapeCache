# VapeCache.Extensions.AspNetCore

ASP.NET Core pipeline hooks for MVC, Minimal APIs, and Blazor output caching backed by VapeCache.

## Install

```bash
dotnet add package VapeCache.Extensions.AspNetCore
```

## Setup

```csharp
using Microsoft.AspNetCore.OutputCaching;
using VapeCache.Abstractions.Connections;
using VapeCache.Extensions.AspNetCore;

builder.Services.AddOptions<RedisConnectionOptions>()
    .Bind(builder.Configuration.GetSection("RedisConnection"));
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

### Named policy registry (dev-friendly)

```csharp
builder.Services.AddVapeCacheAspNetPolicies(policies =>
{
    policies.AddPolicy("products", policy => policy
        .Ttl(TimeSpan.FromMinutes(5))
        .Tags("products", "catalog")
        .VaryByQuery()
        .VaryByHeaders("x-tenant-id"));
});

app.MapGet("/products/{id:int}", async (int id) => $"product:{id}")
    .CacheWithVapeCache("products");
```

### Inline minimal API policy

```csharp
app.MapGet("/search", (string q) => Results.Ok($"query:{q}"))
    .CacheWithVapeCache(policy => policy
        .Ttl(TimeSpan.FromSeconds(60))
        .VaryByQuery()
        .Tags("search"));
```

### MVC/controller attribute

```csharp
[VapeCachePolicy("products", TtlSeconds = 300, VaryByQuery = true, CacheTags = new[] { "products" })]
public IActionResult GetProduct(int id) => Ok(new { id });
```

For MVC/Blazor endpoints, use ASP.NET Core output cache policies/attributes as usual.  
This package replaces the default output-cache store with `VapeCacheOutputCacheStore`.
When `EnableTagIndexing = true`, tag invalidation metadata is stored in VapeCache so tag evictions work across nodes and survive process restarts.

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
