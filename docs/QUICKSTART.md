# VapeCache QuickStart (No-Nonsense)

This is the fastest clean path from zero to a working cache endpoint.
Copy/paste it in order and you are up.

## Prerequisites

- .NET 10 SDK
- Redis 7+
- A Web API project

## 1. Install Packages

```bash
dotnet add package VapeCache
```

Optional, if you are using Aspire:

```bash
dotnet add package VapeCache.Extensions.Aspire
```

## 2. Run Redis

```bash
docker run --name vapecache-redis -p 6379:6379 -d redis:7
```

## 3. Add `appsettings.json`

```json
{
  "RedisConnection": {
    "Host": "localhost",
    "Port": 6379,
    "Database": 0
  },
  "CacheStampede": {
    "Profile": "Balanced"
  }
}
```

Equivalent environment variable:

```bash
setx VAPECACHE_REDIS_CONNECTIONSTRING "redis://localhost:6379/0"
```

## 4. Register VapeCache in `Program.cs`

Use this when wiring services directly:

```csharp
using VapeCache.Abstractions.Caching;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Connections;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddVapecacheRedisConnections();
builder.Services.AddVapecacheCaching();

builder.Services.AddOptions<CacheStampedeOptions>()
    .UseCacheStampedeProfile(CacheStampedeProfile.Balanced)
    .ConfigureCacheStampede(options =>
    {
        options.WithMaxKeys(50_000)
            .WithLockWaitTimeout(TimeSpan.FromMilliseconds(750))
            .WithFailureBackoff(TimeSpan.FromMilliseconds(500));
    })
    .Bind(builder.Configuration.GetSection("CacheStampede"));
```

Use this when you are on Aspire and want one fluent chain:

```csharp
builder.AddVapeCache()
    .WithRedisFromAspire("redis")
    .WithHealthChecks()
    .WithAspireTelemetry()
    .WithCacheStampedeProfile(CacheStampedeProfile.Balanced)
    .WithAutoMappedEndpoints();
```

## 5. Add a Typed Cache Service

```csharp
using VapeCache.Abstractions.Caching;

public sealed class ProductCacheService(IVapeCache cache, IProductRepository repository)
{
    public ValueTask<ProductDto> GetAsync(int id, CancellationToken ct)
    {
        var key = CacheKey<ProductDto>.From($"products:{id}");
        var options = new CacheEntryOptions(
            Ttl: TimeSpan.FromMinutes(10),
            Intent: new CacheIntent(
                CacheIntentKind.ReadThrough,
                Reason: "Product details page"));

        return cache.GetOrCreateAsync(
            key,
            token => new ValueTask<ProductDto>(repository.GetByIdAsync(id, token)),
            options,
            ct);
    }
}
```

## 6. Expose One Endpoint

```csharp
var app = builder.Build();

app.MapGet("/products/{id:int}", async (int id, ProductCacheService service, CancellationToken ct) =>
{
    var product = await service.GetAsync(id, ct);
    return Results.Ok(product);
});

app.MapHealthChecks("/health");
app.Run();
```

## 7. Smoke Test

```bash
curl http://localhost:5000/products/42
curl http://localhost:5000/health
```

If auto-mapped wrapper endpoints are enabled:

```bash
curl http://localhost:5000/vapecache/status
curl http://localhost:5000/vapecache/stats
```

## Common Mistakes

- Redis is not running, or wrong host/port in config.
- Registered `AddVapecacheCaching()` but forgot `AddVapecacheRedisConnections()`.
- Invalid `CacheStampede` values (out of allowed ranges).
- Breaker control endpoints exposed publicly without auth.

## Next Docs

- [CONFIGURATION.md](CONFIGURATION.md)
- [API_REFERENCE.md](API_REFERENCE.md)
- [ASPIRE_INTEGRATION.md](ASPIRE_INTEGRATION.md)
- [WRAPPER_PLUGIN_GUIDE.md](WRAPPER_PLUGIN_GUIDE.md)
