<p align="center">
  <img src="assets/vapecache-brand.png" alt="VapeCache logo" width="920" />
</p>

# VapeCache Enterprise

VapeCache Enterprise is a Redis-first caching platform for .NET 10 built for high-throughput APIs that need predictable behavior during Redis instability, strong observability, and practical operational control.

## Why Teams Use VapeCache

- Fast cache-path Redis transport designed for caching workloads, not generic Redis admin use.
- Hybrid failover with circuit breaker so Redis incidents do not take down the app path.
- Stampede protection for hot keys with configurable lock and failure-backoff controls.
- Strong telemetry surface (OpenTelemetry metrics/traces + structured logging).
- Enterprise durability options for prolonged outages (spill + reconciliation).

## What This Repository Contains

This repository is the **enterprise distribution**. It includes the OSS runtime plus enterprise packages.

If you only need the runtime, use OSS:

- https://github.com/haxxornulled/VapeCache

### Package Map

| Package / Service | Purpose | In OSS |
|---|---|---|
| `VapeCache` | Core runtime, transport, cache APIs, telemetry | Yes |
| `VapeCache.Abstractions` | Contracts, options, value types | Yes |
| `VapeCache.Extensions.Aspire` | Aspire integration + endpoint wiring | Yes |
| `VapeCache.Extensions.AspNetCore` | ASP.NET Core output-cache store integration | No |
| `VapeCache.Persistence` | Durable spill-to-disk for fallback cache path | No |
| `VapeCache.Reconciliation` | Replay tracked writes/deletes after Redis recovery | No |
| `VapeCache.Licensing` | Enterprise license validation runtime package | No |
| `VapeCache.Licensing.ControlPlane` | Revocation/entitlement control-plane service | No |

## Quick Start

This is the shortest clean path from zero to a working endpoint.

### 1. Prerequisites

- .NET 10 SDK
- Redis 7+
- A Web API project

### 2. Install Package

```bash
dotnet add package VapeCache
```

Optional:

```bash
dotnet add package VapeCache.Extensions.Aspire
dotnet add package VapeCache.Extensions.AspNetCore
```

### 3. Run Redis

```bash
docker run --name vapecache-redis -p 6379:6379 -d redis:7
```

### 4. Add Configuration

`appsettings.json`

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

### 5. Register Services (`Program.cs`)

```csharp
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Connections;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<RedisConnectionOptions>()
    .Bind(builder.Configuration.GetSection("RedisConnection"));

builder.Services.AddVapecacheRedisConnections();
builder.Services.AddVapecacheCaching();

builder.Services.AddOptions<CacheStampedeOptions>()
    .UseCacheStampedeProfile(CacheStampedeProfile.Balanced)
    .Bind(builder.Configuration.GetSection("CacheStampede"));
```

### 6. Use Typed Cache API

```csharp
using VapeCache.Abstractions.Caching;

public sealed class ProductCacheService(IVapeCache cache, IProductRepository repo)
{
    public ValueTask<ProductDto> GetAsync(int id, CancellationToken ct)
    {
        var key = CacheKey<ProductDto>.From($"products:{id}");
        var options = new CacheEntryOptions(
            Ttl: TimeSpan.FromMinutes(10),
            Intent: new CacheIntent(
                CacheIntentKind.ReadThrough,
                Reason: "Product details"))
            .WithZone("ef:products");

        return cache.GetOrCreateAsync(
            key,
            token => new ValueTask<ProductDto>(repo.GetByIdAsync(id, token)),
            options,
            ct);
    }
}
```

### 7. Expose One Endpoint

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

### 8. Smoke Test

```bash
curl http://localhost:5000/products/42
curl http://localhost:5000/health
```

If wrapper endpoints are enabled in your host:

```bash
curl http://localhost:5000/vapecache/status
curl http://localhost:5000/vapecache/stats
```

## Common Mistakes

- Redis not reachable (wrong host/port/TLS).
- Registered `AddVapecacheCaching()` but forgot `AddVapecacheRedisConnections()`.
- `RedisConnection` not bound and no `VAPECACHE_REDIS_CONNECTIONSTRING` set.
- Invalid `CacheStampede` values outside validated range.
- Exposing operational endpoints publicly without auth.

## Enterprise Add-Ons (Durability + Recovery)

For enterprise-only packages (`VapeCache.Persistence`, `VapeCache.Reconciliation`):

```bash
setx VAPECACHE_LICENSE_KEY "VC2...."
setx VAPECACHE_LICENSE_REVOCATION_ENABLED "true"
setx VAPECACHE_LICENSE_REVOCATION_ENDPOINT "https://license-control-plane.internal"
setx VAPECACHE_LICENSE_REVOCATION_API_KEY "<secret>"
```

These packages are intended for no-drop outage handling where in-memory-only fallback is insufficient.

## ASP.NET Core Output Caching

```csharp
builder.Services.AddVapeCacheOutputCaching(options =>
{
    options.AddBasePolicy(policy => policy.Expire(TimeSpan.FromSeconds(30)));
});

var app = builder.Build();
app.UseVapeCacheOutputCaching();
```

## Build and Test

```bash
dotnet build VapeCache.sln -c Release
```

```bash
dotnet test VapeCache.Tests/VapeCache.Tests.csproj -c Release
dotnet test VapeCache.PerfGates.Tests/VapeCache.PerfGates.Tests.csproj -c Release
```

## Documentation

Start here:

- [docs/INDEX.md](docs/INDEX.md)
- [docs/QUICKSTART.md](docs/QUICKSTART.md)
- [docs/CONFIGURATION.md](docs/CONFIGURATION.md)
- [docs/API_REFERENCE.md](docs/API_REFERENCE.md)
- [docs/HYBRID_CACHING_API_SURFACE.md](docs/HYBRID_CACHING_API_SURFACE.md)
- [docs/CACHE_TAGS_AND_ZONES.md](docs/CACHE_TAGS_AND_ZONES.md)
- [docs/LOGGING_TELEMETRY_CONFIGURATION.md](docs/LOGGING_TELEMETRY_CONFIGURATION.md)
- [docs/ASPIRE_INTEGRATION.md](docs/ASPIRE_INTEGRATION.md)
- [docs/PERFORMANCE.md](docs/PERFORMANCE.md)
- [docs/UPGRADE_NOTES.md](docs/UPGRADE_NOTES.md)

## License

Community use is licensed under [LICENSE](LICENSE) (PolyForm Noncommercial 1.0.0 with required notices).

Commercial use requires an enterprise license.

Enterprise package commercial terms are in [LICENSE-ENTERPRISE.txt](LICENSE-ENTERPRISE.txt).

Operational licensing guidance is in [COMMERCIAL-LICENSING.md](COMMERCIAL-LICENSING.md).
