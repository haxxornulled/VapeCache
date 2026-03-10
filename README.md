<p align="center">
  <img src="assets/vapecache-brand.png" alt="VapeCache logo" width="920" />
</p>

# VapeCache

VapeCache is a Redis-first caching runtime for ASP.NET Core and .NET services.
It is designed for predictable behavior under load, Redis trouble, and high-throughput API traffic.

- Redis transport tuned for cache workloads
- Circuit-breaker and in-memory fallback for outage tolerance
- Stampede controls for hot keys
- OpenTelemetry metrics + traces
- ASP.NET Core and Aspire integrations

OSS scope in this repository: production-ready runtime packages for core caching, invalidation, ASP.NET Core integration, and Aspire integration.

## Maturity and Evidence

Current project status: `Early Adoption`.

- Production-capable features are present, but ecosystem adoption is still building.
- Benchmark claims follow strict disclosure rules in [docs/BENCHMARK_CLAIMS_POLICY.md](docs/BENCHMARK_CLAIMS_POLICY.md).
- Release and compatibility governance is documented in:
  - [docs/STABILITY_POLICY.md](docs/STABILITY_POLICY.md)
  - [docs/PRODUCTION_READINESS.md](docs/PRODUCTION_READINESS.md)
  - [docs/PACKAGE_COMPATIBILITY_PLAN.md](docs/PACKAGE_COMPATIBILITY_PLAN.md)

## QuickStart

1. Install packages

```bash
dotnet add package VapeCache.Runtime
dotnet add package VapeCache.Extensions.Aspire
```

If you need ASP.NET Core output-cache middleware integration:

```bash
dotnet add package VapeCache.Extensions.AspNetCore
```

If you want a DI composition facade for clean architecture wiring:

```bash
dotnet add package VapeCache.Extensions.DependencyInjection
```

2. Run Redis

```bash
docker run --name vapecache-redis -p 6379:6379 -d redis:7
```

3. Configure `appsettings.json`

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

4. Register VapeCache in `Program.cs`

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

5. Add one endpoint

```csharp
var app = builder.Build();

app.MapGet("/products/{id:int}", async (int id, IVapeCache cache, CancellationToken ct) =>
{
    var key = CacheKey<string>.From($"products:{id}");
    var value = await cache.GetOrCreateAsync(
        key,
        _ => new ValueTask<string>($"product-{id}"),
        new CacheEntryOptions(TimeSpan.FromMinutes(5)),
        ct);

    return Results.Ok(value);
});

app.Run();
```

6. Go deeper: [docs/QUICKSTART.md](docs/QUICKSTART.md)

## Production Packages (OSS)

| Package | Purpose |
|---|---|
| `VapeCache.Runtime` | Core runtime, Redis transport, fallback behavior, telemetry |
| `VapeCache.Core` | Shared primitives package (transitive dependency, usually not installed directly) |
| `VapeCache.Abstractions` | Public contracts and option/value types |
| `VapeCache.Features.Invalidation` | Optional key/tag/zone invalidation policies |
| `VapeCache.Extensions.DependencyInjection` | One-call IServiceCollection wiring facade for runtime + config binding |
| `VapeCache.Extensions.AspNetCore` | ASP.NET Core output-cache integration |
| `VapeCache.Extensions.Aspire` | Aspire wiring, health checks, endpoint helpers |

## Out Of OSS Scope

The following are not shipped from this OSS repository:

- enterprise licensing and control-plane features
- durable spill persistence package
- reconciliation package for post-outage write replay

## Documentation

- [docs/INDEX.md](docs/INDEX.md)
- [docs/QUICKSTART.md](docs/QUICKSTART.md)
- [docs/CONFIGURATION.md](docs/CONFIGURATION.md)
- [docs/SETTINGS_REFERENCE.md](docs/SETTINGS_REFERENCE.md)
- [docs/CACHE_INVALIDATION.md](docs/CACHE_INVALIDATION.md)
- [docs/ASPNETCORE_PIPELINE_CACHING.md](docs/ASPNETCORE_PIPELINE_CACHING.md)
- [docs/ASPIRE_INTEGRATION.md](docs/ASPIRE_INTEGRATION.md)
- [docs/STABILITY_POLICY.md](docs/STABILITY_POLICY.md)
- [docs/PRODUCTION_READINESS.md](docs/PRODUCTION_READINESS.md)
- [docs/RELEASE_RUNBOOK.md](docs/RELEASE_RUNBOOK.md)

## Build And Test

```bash
dotnet build VapeCache.slnx -c Release
dotnet test VapeCache.Tests/VapeCache.Tests.csproj -c Release
```

## License

- Community/non-commercial usage: [LICENSE](LICENSE)
- Commercial usage: enterprise license required
