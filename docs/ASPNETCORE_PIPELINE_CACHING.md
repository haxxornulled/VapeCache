# ASP.NET Core Pipeline Caching

`VapeCache.Extensions.AspNetCore` plugs VapeCache into ASP.NET Core output caching so MVC, Minimal APIs, and Blazor endpoints can cache full responses using VapeCache storage.

## Why This Hook Exists

- Native output caching policy model in ASP.NET Core is solid.
- Default store is memory-first and process-local.
- This integration keeps the policy/middleware model but swaps storage to VapeCache.

## Package

```bash
dotnet add package VapeCache.Extensions.AspNetCore
```

## Service Registration

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
    store.EnableTagIndexing = true;
});
```

## Pipeline Hook

```csharp
var app = builder.Build();
app.UseVapeCacheOutputCaching();
```

## Endpoint Hooks

Minimal API:

```csharp
app.MapGet("/products/{id:int}", async (int id) => Results.Ok(new { id }))
   .CacheWithVapeCache();

var api = app.MapGroup("/api")
   .CacheWithVapeCache(policy => policy
       .Ttl(TimeSpan.FromSeconds(30))
       .Tags("api-group"));

api.MapGet("/products/{id:int}", (int id) => Results.Ok(new { id }));
```

MVC / Razor / Blazor Web App:

- Keep using normal ASP.NET Core output-cache policy/attributes.
- Storage is already replaced by `VapeCacheOutputCacheStore`.

## Store Options

`VapeCacheOutputCacheStoreOptions`:

- `KeyPrefix`:
  - Prefix for all output cache keys.
- `DefaultTtl`:
  - Used when middleware requests non-positive duration.
- `EnableTagIndexing`:
  - Enables shared tag-version metadata in VapeCache so `EvictByTag` works across nodes and survives restarts.

## Aspire Fluent Hook

```csharp
builder.AddVapeCache()
    .WithAspNetCoreOutputCaching(options =>
    {
        options.AddBasePolicy(policy => policy.Expire(TimeSpan.FromSeconds(30)));
    });
```

## Sticky Sessions in Cluster/Web-Garden Failover

Local in-memory fallback is node-local. In a cluster/web-garden, failover continuity for session-like keys depends on affinity.

Use the affinity hint middleware to emit node headers/cookie while failover is active:

```csharp
builder.Services.AddVapeCacheFailoverAffinityHints(options =>
{
    options.NodeId = Environment.MachineName;
    options.CookieName = "VapeCacheAffinity";
});

var app = builder.Build();
app.UseVapeCacheFailoverAffinityHints();
```

Middleware outputs:

- `X-VapeCache-Node`
- `X-VapeCache-Failover-State`
- optional `X-VapeCache-Affinity-Mismatch` when request cookie targets another node
