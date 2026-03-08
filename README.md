<p align="center">
  <img src="assets/vapecache-brand.png" alt="VapeCache logo" width="920" />
</p>

# VapeCache Enterprise

VapeCache Enterprise is a Redis-first caching platform for `.NET 10` built for teams that need high throughput, predictable failure behavior, and strong operational visibility.

## Why Teams Pick It

- Fast cache-path transport tuned for caching workloads.
- Hybrid failover + circuit breaker to keep app paths alive during Redis incidents.
- Stampede protection for hot keys with practical tuning controls.
- Strong telemetry (OpenTelemetry + structured logging).
- Enterprise durability options for longer outages (persistence + reconciliation).

## Enterprise vs OSS

This repo is the **enterprise distribution**.

- Enterprise repo: `VapeCache-Enterprise` (this repo)
- OSS runtime: https://github.com/haxxornulled/VapeCache

### Package Map

| Package / Service | Purpose | In OSS |
|---|---|---|
| `VapeCache` | Core runtime, transport, cache APIs, telemetry | Yes |
| `VapeCache.Abstractions` | Contracts, options, value types | Yes |
| `VapeCache.Extensions.Aspire` | Aspire integration + endpoint wiring | Yes |
| `VapeCache.Extensions.AspNetCore` | ASP.NET Core output-cache integration | No |
| `VapeCache.Persistence` | Durable spill-to-disk for fallback cache path | No |
| `VapeCache.Reconciliation` | Replay tracked writes/deletes after Redis recovery | No |
| `VapeCache.Licensing` | Enterprise license validation package | No |
| `VapeCache.Licensing.ControlPlane` | Revocation/entitlement control plane | No |

## Quick Start (No-Nonsense)

This is the shortest clean path from zero to a working cached endpoint.

### 1. Prerequisites

- .NET 10 SDK
- Redis 7+
- A Web API project

### 2. Install Packages

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

### 4. Configure Connection

`appsettings.json`:

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

Optional cluster/RESP3 settings:

```json
{
  "RedisConnection": {
    "RespProtocolVersion": 3,
    "EnableClusterRedirection": true,
    "MaxClusterRedirects": 3
  }
}
```

### 5. Register Services (`Program.cs`)

#### Direct Wiring

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
    .ConfigureCacheStampede(options =>
    {
        options.WithMaxKeys(50_000)
            .WithLockWaitTimeout(TimeSpan.FromMilliseconds(750))
            .WithFailureBackoff(TimeSpan.FromMilliseconds(500));
    })
    .Bind(builder.Configuration.GetSection("CacheStampede"));
```

#### Aspire Fluent Setup

```csharp
builder.AddVapeCache()
    .WithRedisFromAspire("redis")
    .WithHealthChecks()
    .WithAspireTelemetry()
    .WithCacheStampedeProfile(CacheStampedeProfile.Balanced)
    .WithAutoMappedEndpoints(options =>
    {
        options.Enabled = true;
    });
```

### 6. Add a Typed Cache Service

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
                Reason: "Product details page"))
            .WithZone("ef:products");

        return cache.GetOrCreateAsync(
            key,
            token => new ValueTask<ProductDto>(repository.GetByIdAsync(id, token)),
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

If wrapper endpoints are enabled:

```bash
curl http://localhost:5000/vapecache/status
curl http://localhost:5000/vapecache/stats
```

## Bonus: Large Payload Streaming

For chunked large payloads (for example media or model artifacts), use `ICacheChunkStreamService`:

```csharp
app.MapPut("/media/{id}", async (string id, HttpRequest request, ICacheChunkStreamService streams, CancellationToken ct) =>
{
    var manifest = await streams.WriteAsync(
        $"media:{id}",
        request.Body,
        new CacheEntryOptions(Ttl: TimeSpan.FromMinutes(30)),
        new CacheChunkStreamWriteOptions
        {
            ChunkSizeBytes = 64 * 1024,
            ContentType = request.ContentType
        },
        ct);

    return Results.Ok(manifest);
});
```

## Common Mistakes

- Redis not reachable (wrong host/port/TLS/credentials).
- Registered `AddVapecacheCaching()` but forgot `AddVapecacheRedisConnections()`.
- `RedisConnection` not bound and no `VAPECACHE_REDIS_CONNECTIONSTRING`.
- `CacheStampede` values outside valid ranges.
- Exposing operational endpoints publicly without auth.

## Documentation Coverage

- Full options/defaults reference (generated from source): [docs/SETTINGS_REFERENCE.md](docs/SETTINGS_REFERENCE.md)
- Configuration deep dive: [docs/CONFIGURATION.md](docs/CONFIGURATION.md)
- Quick setup: [QUICK_START.md](QUICK_START.md)

## Benchmarks

- Latest posted benchmark summary: [PHASE1_BENCHMARK_REPORT.md](PHASE1_BENCHMARK_REPORT.md)
- Grocery Store benchmark harness guide: [docs/GROCERY_STORE_DEMO.md](docs/GROCERY_STORE_DEMO.md)
- Performance notes and scripts: [docs/PERFORMANCE.md](docs/PERFORMANCE.md)
- Benchmark claims policy: [docs/BENCHMARK_CLAIMS_POLICY.md](docs/BENCHMARK_CLAIMS_POLICY.md)

Benchmark claims are published in two classes:
- **Strict/Fair (authoritative):** same knobs across tracks/providers (`-DisableTrackDefaults`).
- **Tuned/Showcase (engineering):** workload-tuned settings, always labeled as tuned.
- **Validation rigor:** benchmark claims are backed by `Release` build + full test pass + perf-gate test pass on the same code snapshot.

## Enterprise License Runtime (Optional but Recommended)

For enterprise durability features (`VapeCache.Persistence`, `VapeCache.Reconciliation`):

```bash
setx VAPECACHE_LICENSE_KEY "VC2...."
setx VAPECACHE_LICENSE_REVOCATION_ENABLED "true"
setx VAPECACHE_LICENSE_REVOCATION_ENDPOINT "https://license-control-plane.internal"
setx VAPECACHE_LICENSE_REVOCATION_API_KEY "<secret>"
```

## ASP.NET Core Output Caching Hook

```csharp
builder.Services.AddVapeCacheOutputCaching(options =>
{
    options.AddBasePolicy(policy => policy.Expire(TimeSpan.FromSeconds(30)));
});

var app = builder.Build();
app.UseVapeCacheOutputCaching();
```

Optional failover affinity hints for multi-node setups:

```csharp
builder.Services.AddVapeCacheFailoverAffinityHints();
var app = builder.Build();
app.UseVapeCacheFailoverAffinityHints();
```

## Build and Test

```bash
dotnet build VapeCache.sln -c Release
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
- [docs/ASPNETCORE_PIPELINE_CACHING.md](docs/ASPNETCORE_PIPELINE_CACHING.md)

## License

Community use is licensed under [LICENSE](LICENSE) (PolyForm Noncommercial 1.0.0 with required notices).

Commercial use requires an enterprise license.

Enterprise package commercial terms are in [LICENSE-ENTERPRISE.txt](LICENSE-ENTERPRISE.txt).

Operational licensing guidance is in [COMMERCIAL-LICENSING.md](COMMERCIAL-LICENSING.md).
