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

## Performance Transparency

Performance data and methodology are tracked in-repo with reproducible benchmark artifacts and disclosure rules.

- Benchmark methodology: [docs/BENCHMARKING.md](docs/BENCHMARKING.md)
- Claims policy: [docs/BENCHMARK_CLAIMS_POLICY.md](docs/BENCHMARK_CLAIMS_POLICY.md)
- Latest benchmark reports: [docs/BENCHMARK_RESULTS.md](docs/BENCHMARK_RESULTS.md)

OSS scope in this repository: production-ready runtime packages for core caching, invalidation, ASP.NET Core integration, and Aspire integration.
For OSS/Enterprise boundaries, see [docs/OSS_VS_ENTERPRISE.md](docs/OSS_VS_ENTERPRISE.md).

## Maturity and Evidence

Current project status: `Production-Capable`.

- Production runtime features are in place, including failover paths, stampede controls, reconnect handling, and observability.
- Stability, compatibility, and release gates are documented and enforced in-repo.
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

If you need an `IDistributedCache` / `IBufferDistributedCache` bridge for interoperability or migration:

```bash
dotnet add package VapeCache.Extensions.DistributedCache
```

If you want centralized Serilog + OTEL logging wiring with rolling file sink support and optional JSON formatting:

```bash
dotnet add package VapeCache.Extensions.Logging
```

If you need Redis pub/sub support:

```bash
dotnet add package VapeCache.Extensions.PubSub
```

If you need Redis 8.6 stream idempotent producer support:

```bash
dotnet add package VapeCache.Extensions.Streams
```

If you need HASH-backed RediSearch projections for operational lookup/search workloads:

```bash
dotnet add package VapeCache.Features.Search
```

If you need EF Core second-level cache interceptor contracts and invalidation bridge wiring:

```bash
dotnet add package VapeCache.Extensions.EntityFrameworkCore
```

If you need EF Core cache OpenTelemetry signals (Aspire/OTEL ready):

```bash
dotnet add package VapeCache.Extensions.EntityFrameworkCore.OpenTelemetry
```

2. Run Redis

```bash
docker run --name vapecache-redis -p 6379:6379 -d redis:7
```

If you do not have Redis and want a local/lightweight runtime, skip Redis and use `AddVapeCacheInMemory(...)` instead.

3. Configure `appsettings.json`

```json
{
  "RedisConnection": {
    "Host": "localhost",
    "Port": 6379,
    "Database": 0
  }
}
```

4. Register VapeCache in `Program.cs`

```csharp
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Extensions.Logging;
using VapeCache.Extensions.PubSub;
using VapeCache.Extensions.Streams;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Connections;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<RedisConnectionOptions>()
    .Bind(builder.Configuration.GetSection("RedisConnection"));

builder.Services.AddVapecacheRedisConnections();
builder.Services.AddVapecacheCaching();
builder.Services.AddVapeCachePubSub(); // optional: only when pub/sub is needed
builder.Services.AddVapeCacheStreams(); // optional: only when stream idempotent producer support is needed

builder.Services.AddOptions<CacheStampedeOptions>()
    .UseCacheStampedeProfile(CacheStampedeProfile.Balanced)
    .Bind(builder.Configuration.GetSection("CacheStampede"));
```

Memory-only alternative for local dev or lightweight single-node hosts:

```csharp
using VapeCache.Abstractions.Caching;
using VapeCache.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddVapeCacheInMemory(builder.Configuration)
    .WithCacheStampedeProfile(CacheStampedeProfile.Balanced);
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

## ASP.NET Core Policy Ergonomics

VapeCache now supports native ASP.NET Core output-cache ergonomics for both minimal APIs and MVC while keeping the runtime engine untouched.

```csharp
builder.Services.AddVapeCacheOutputCaching();
builder.Services.AddVapeCacheAspNetPolicies(policies =>
{
    policies.AddPolicy("products", policy => policy
        .Ttl(TimeSpan.FromMinutes(5))
        .VaryByQuery()
        .Tags("products"));
});

app.MapGet("/products/{id:int}", (int id) => Results.Ok(new { id }))
   .CacheWithVapeCache("products");
```

MVC/controller attributes are also supported:

```csharp
[VapeCachePolicy("products", TtlSeconds = 300, VaryByQuery = true, CacheTags = new[] { "products" })]
public IActionResult GetProduct(int id) => Ok(new { id });
```

See:
- [docs/ASPNETCORE_POLICY_EXTENSION.md](docs/ASPNETCORE_POLICY_EXTENSION.md)
- [docs/ASPNETCORE_PIPELINE_CACHING.md](docs/ASPNETCORE_PIPELINE_CACHING.md)

## IDistributedCache Bridge

Already on `IDistributedCache` or using FusionCache with a distributed L2?
VapeCache ships a bridge package for that migration path.

```csharp
using VapeCache.Extensions.DistributedCache;

builder.Services.AddVapeCache(builder.Configuration)
    .UseDistributedCacheAdapter(options =>
    {
        options.KeyPrefix = "fusion:l2:";
    });
```

This is intentionally a compatibility layer, not the preferred headline integration.
Native VapeCache remains the recommended path when you want the full runtime surface.
Recommended framing: keep your current cache abstraction, route the distributed-cache layer through VapeCache, and migrate to native APIs later if you want the fuller runtime model.
See [docs/DISTRIBUTED_CACHE_BRIDGE.md](docs/DISTRIBUTED_CACHE_BRIDGE.md) for the interop positioning and FusionCache guidance.

## Redis Search Projections

For workloads like grocery receipt verification, the recommended plan is to search denormalized HASH projections, not your full source aggregates.

`VapeCache.Features.Search` gives you:

- typed `TEXT`, `TAG`, and `NUMERIC` RediSearch schemas
- generic HASH projection storage
- query-builder helpers for exact-match, text, and numeric range filters
- search-result cache key/tag conventions that fit `VapeCache.Features.Invalidation`

That lets the front-door receipt check invalidate instantly without flattening the rest of the runtime behind a generic search abstraction.
See [docs/REDIS_SEARCH.md](docs/REDIS_SEARCH.md).

## Production Packages (OSS)

| Package | NuGet | GitHub Packages | Purpose | Docs |
|---|---|---|---|---|
| `VapeCache.Runtime` | [VapeCache.Runtime](https://www.nuget.org/packages/VapeCache.Runtime) | [vapecache.runtime](https://github.com/users/haxxornulled/packages/nuget/package/vapecache.runtime) | Core runtime, Redis transport, fallback behavior, telemetry | [API Reference](docs/API_REFERENCE.md) |
| `VapeCache.Core` | [VapeCache.Core](https://www.nuget.org/packages/VapeCache.Core) | [vapecache.core](https://github.com/users/haxxornulled/packages/nuget/package/vapecache.core) | Shared primitives package (transitive dependency, usually not installed directly) | [Package Matrix](docs/NUGET_PACKAGES.md) |
| `VapeCache.Abstractions` | [VapeCache.Abstractions](https://www.nuget.org/packages/VapeCache.Abstractions) | [vapecache.abstractions](https://github.com/users/haxxornulled/packages/nuget/package/vapecache.abstractions) | Public contracts and option/value types | [API Reference](docs/API_REFERENCE.md) |
| `VapeCache.Features.Invalidation` | [VapeCache.Features.Invalidation](https://www.nuget.org/packages/VapeCache.Features.Invalidation) | [vapecache.features.invalidation](https://github.com/users/haxxornulled/packages/nuget/package/vapecache.features.invalidation) | Optional key/tag/zone invalidation policies | [Cache Invalidation](docs/CACHE_INVALIDATION.md) |
| `VapeCache.Features.Search` | [VapeCache.Features.Search](https://www.nuget.org/packages/VapeCache.Features.Search) | [vapecache.features.search](https://github.com/users/haxxornulled/packages/nuget/package/vapecache.features.search) | Typed HASH-backed RediSearch projections, query helpers, and invalidation conventions for operational search | [Redis Search](docs/REDIS_SEARCH.md) |
| `VapeCache.Extensions.DependencyInjection` | [VapeCache.Extensions.DependencyInjection](https://www.nuget.org/packages/VapeCache.Extensions.DependencyInjection) | [vapecache.extensions.dependencyinjection](https://github.com/users/haxxornulled/packages/nuget/package/vapecache.extensions.dependencyinjection) | One-call IServiceCollection wiring facade for runtime + config binding | [Quickstart](docs/QUICKSTART.md) |
| `VapeCache.Extensions.DistributedCache` | [VapeCache.Extensions.DistributedCache](https://www.nuget.org/packages/VapeCache.Extensions.DistributedCache) | [vapecache.extensions.distributedcache](https://github.com/users/haxxornulled/packages/nuget/package/vapecache.extensions.distributedcache) | `IDistributedCache` / `IBufferDistributedCache` bridge for interoperability and migration | [Package README](VapeCache.Extensions.DistributedCache/README.md) |
| `VapeCache.Extensions.Logging` | [VapeCache.Extensions.Logging](https://www.nuget.org/packages/VapeCache.Extensions.Logging) | [vapecache.extensions.logging](https://github.com/users/haxxornulled/packages/nuget/package/vapecache.extensions.logging) | Optional Serilog + OTEL logging wiring with file/Seq/console sinks and pluggable JSON formatting | [Logging + Telemetry](docs/LOGGING_TELEMETRY_CONFIGURATION.md) |
| `VapeCache.Extensions.PubSub` | [VapeCache.Extensions.PubSub](https://www.nuget.org/packages/VapeCache.Extensions.PubSub) | [vapecache.extensions.pubsub](https://github.com/users/haxxornulled/packages/nuget/package/vapecache.extensions.pubsub) | Optional Redis pub/sub package (publish/subscribe, bounded queues, reconnect/resubscribe) | [API Reference](docs/API_REFERENCE.md) |
| `VapeCache.Extensions.Streams` | [VapeCache.Extensions.Streams](https://www.nuget.org/packages/VapeCache.Extensions.Streams) | [vapecache.extensions.streams](https://github.com/users/haxxornulled/packages/nuget/package/vapecache.extensions.streams) | Optional Redis 8.6 streams package for idempotent producers (`XADD IDMP/IDMPAUTO`, `XCFGSET`) | [Package README](VapeCache.Extensions.Streams/README.md) |
| `VapeCache.Extensions.EntityFrameworkCore` | [VapeCache.Extensions.EntityFrameworkCore](https://www.nuget.org/packages/VapeCache.Extensions.EntityFrameworkCore) | [vapecache.extensions.entityframeworkcore](https://github.com/users/haxxornulled/packages/nuget/package/vapecache.extensions.entityframeworkcore) | EF Core second-level cache interceptor contracts, deterministic query-key builder, and SaveChanges invalidation bridge wiring | [EF Core Second-Level Cache](docs/EFCORE_SECOND_LEVEL_CACHE.md) |
| `VapeCache.Extensions.EntityFrameworkCore.OpenTelemetry` | [VapeCache.Extensions.EntityFrameworkCore.OpenTelemetry](https://www.nuget.org/packages/VapeCache.Extensions.EntityFrameworkCore.OpenTelemetry) | [vapecache.extensions.entityframeworkcore.opentelemetry](https://github.com/users/haxxornulled/packages/nuget/package/vapecache.extensions.entityframeworkcore.opentelemetry) | OpenTelemetry metrics/activity package for EF Core cache interceptor events and profiler correlation | [EF Core package README](VapeCache.Extensions.EntityFrameworkCore.OpenTelemetry/README.md) |
| `VapeCache.Extensions.AspNetCore` | [VapeCache.Extensions.AspNetCore](https://www.nuget.org/packages/VapeCache.Extensions.AspNetCore) | [vapecache.extensions.aspnetcore](https://github.com/users/haxxornulled/packages/nuget/package/vapecache.extensions.aspnetcore) | ASP.NET Core output-cache integration | [ASP.NET Core Pipeline](docs/ASPNETCORE_PIPELINE_CACHING.md) |
| `VapeCache.Extensions.Aspire` | [VapeCache.Extensions.Aspire](https://www.nuget.org/packages/VapeCache.Extensions.Aspire) | [vapecache.extensions.aspire](https://github.com/users/haxxornulled/packages/nuget/package/vapecache.extensions.aspire) | Aspire wiring, health checks, endpoint helpers | [Aspire Integration](docs/ASPIRE_INTEGRATION.md) |

Full package install matrix: [docs/NUGET_PACKAGES.md](docs/NUGET_PACKAGES.md)
GitHub profile/About/topic/social branding: [docs/GITHUB_BRANDING.md](docs/GITHUB_BRANDING.md)

## Out Of OSS Scope

The following are not shipped from this OSS repository:

- adaptive autoscaling of multiplexed lanes
- enterprise licensing and control-plane features
- durable spill persistence package
- reconciliation package for post-outage write replay

Multiplexing itself is OSS; adaptive autoscaling is Enterprise.

## Documentation

- Start here: [docs/INDEX.md](docs/INDEX.md)
- Getting started: [docs/QUICKSTART.md](docs/QUICKSTART.md), [docs/CONFIGURATION.md](docs/CONFIGURATION.md), [docs/SETTINGS_REFERENCE.md](docs/SETTINGS_REFERENCE.md), [docs/NUGET_PACKAGES.md](docs/NUGET_PACKAGES.md), [docs/GITHUB_BRANDING.md](docs/GITHUB_BRANDING.md)
- Core runtime: [docs/API_REFERENCE.md](docs/API_REFERENCE.md), [docs/CACHE_INVALIDATION.md](docs/CACHE_INVALIDATION.md), [docs/CACHE_TAGS_AND_ZONES.md](docs/CACHE_TAGS_AND_ZONES.md)
- ASP.NET Core: [docs/ASPNETCORE_PIPELINE_CACHING.md](docs/ASPNETCORE_PIPELINE_CACHING.md), [docs/ASPNETCORE_POLICY_EXTENSION.md](docs/ASPNETCORE_POLICY_EXTENSION.md)
- Integrations: [docs/ASPIRE_INTEGRATION.md](docs/ASPIRE_INTEGRATION.md), [docs/LOGGING_TELEMETRY_CONFIGURATION.md](docs/LOGGING_TELEMETRY_CONFIGURATION.md)
- Ops and releases: [docs/PRODUCTION_GUARDRAILS.md](docs/PRODUCTION_GUARDRAILS.md), [docs/STABILITY_POLICY.md](docs/STABILITY_POLICY.md), [docs/PRODUCTION_READINESS.md](docs/PRODUCTION_READINESS.md), [docs/RELEASE_RUNBOOK.md](docs/RELEASE_RUNBOOK.md)
- Process model: [docs/PROCESS_MODEL.md](docs/PROCESS_MODEL.md)
- OSS and licensing: [docs/OSS_VS_ENTERPRISE.md](docs/OSS_VS_ENTERPRISE.md), [docs/LICENSE_FAQ.md](docs/LICENSE_FAQ.md)

## Build And Test

```bash
dotnet build VapeCache.slnx -c Release
dotnet test VapeCache.Tests/VapeCache.Tests.csproj -c Release
```

## License

VapeCache OSS is licensed under the MIT License.

That means you can use, modify, redistribute, and commercialize the code with very few conditions.
The main obligations are to keep the copyright notice and license text with substantial portions of the software.

Important boundary:

- the code is MIT-licensed
- the `VapeCache` name, logos, package identity, and brand assets are not granted to you under MIT

See [LICENSE](LICENSE) for the license text, [docs/LICENSE_FAQ.md](docs/LICENSE_FAQ.md) for quick answers, and [docs/TRADEMARK_POLICY.md](docs/TRADEMARK_POLICY.md) for brand and naming rules.
