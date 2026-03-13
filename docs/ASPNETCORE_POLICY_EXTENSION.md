# ASP.NET Core Policy Extension Guide

This guide covers the production-safe policy ergonomics layer added in `VapeCache.Extensions.AspNetCore`.

It is additive to existing usage:

- existing `AddVapeCacheOutputCaching(...)` still works
- existing `UseVapeCacheOutputCaching()` still works
- existing `CacheWithVapeCache()` still works

No core runtime internals were moved into ASP.NET-specific layers.

## Goals

- Keep the runtime engine unchanged
- Expose endpoint-level policy intent clearly
- Support minimal APIs and MVC controllers
- Stay native to ASP.NET Core output-cache pipeline in .NET 10

## 1. Register Output Cache + Named Policies

```csharp
using VapeCache.Extensions.AspNetCore;

builder.Services.AddVapeCacheOutputCaching(options =>
{
    options.AddBasePolicy(policy => policy.Expire(TimeSpan.FromSeconds(30)));
});

builder.Services.AddVapeCacheAspNetPolicies(policies =>
{
    policies.AddPolicy("products", policy => policy
        .Ttl(TimeSpan.FromMinutes(5))
        .VaryByQuery()
        .VaryByHeaders("x-tenant-id")
        .Tags("products", "catalog"));
});
```

## 2. Minimal API Usage

Named policy:

```csharp
app.MapGet("/products/{id:int}", (int id) => Results.Ok(new { id }))
    .CacheWithVapeCache("products");
```

Inline policy:

```csharp
app.MapGet("/search", (string q) => Results.Ok(new { q }))
    .CacheWithVapeCache(policy => policy
        .Ttl(TimeSpan.FromSeconds(60))
        .VaryByQuery()
        .Tags("search"));
```

## 3. MVC Attribute Usage

```csharp
using VapeCache.Extensions.AspNetCore;

[ApiController]
[Route("api/[controller]")]
public sealed class ProductsController : ControllerBase
{
    [HttpGet("{id:int}")]
    [VapeCachePolicy(
        "products",
        TtlSeconds = 300,
        VaryByQuery = true,
        VaryByRouteValues = new[] { "id" },
        CacheTags = new[] { "products" })]
    public IActionResult Get(int id) => Ok(new { id });
}
```

## Migration Examples

### Existing minimal API call (no changes needed)

Before:

```csharp
app.MapGet("/products/{id:int}", handler).CacheWithVapeCache();
```

After (still valid):

```csharp
app.MapGet("/products/{id:int}", handler).CacheWithVapeCache();
```

### Existing named `CacheOutput(...)` usage

Before:

```csharp
builder.Services.AddOutputCache(options =>
{
    options.AddPolicy("products", policy =>
    {
        policy.Expire(TimeSpan.FromMinutes(5));
        policy.SetVaryByQuery("*");
        policy.Tag("products");
    });
});
```

After (VapeCache policy registry):

```csharp
builder.Services.AddVapeCacheAspNetPolicies(policies =>
{
    policies.AddPolicy("products", policy => policy
        .Ttl(TimeSpan.FromMinutes(5))
        .VaryByQuery()
        .Tags("products"));
});
```

### Existing MVC `[OutputCache]` usage

Before:

```csharp
[OutputCache(Duration = 300, VaryByQueryKeys = new[] { "*" }, Tags = new[] { "products" })]
public IActionResult Get(int id) => Ok();
```

After (equivalent ergonomic wrapper):

```csharp
[VapeCachePolicy(TtlSeconds = 300, VaryByQuery = true, CacheTags = new[] { "products" })]
public IActionResult Get(int id) => Ok();
```

## Boundaries (Important)

- ASP.NET-only code stays in `VapeCache.Extensions.AspNetCore`
- No `Microsoft.AspNetCore.*` dependencies were introduced to core runtime projects
- Policy APIs map to native output-cache behavior; they do not replace the runtime engine

## Related Docs

- [ASPNETCORE_PIPELINE_CACHING.md](ASPNETCORE_PIPELINE_CACHING.md)
- [API_REFERENCE.md](API_REFERENCE.md)
- [QUICKSTART.md](QUICKSTART.md)
