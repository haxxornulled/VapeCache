<p align="center">
  <img src="assets/vapecache-brand.png" alt="VapeCache logo" width="920" />
</p>

# VapeCache Enterprise

VapeCache Enterprise is a Redis-first caching platform for .NET 10. It is built for teams that need predictable cache behavior under heavy load, clear operational visibility, and controlled failure handling when Redis is unhealthy.

## What This Repo Is

This repository is the enterprise distribution.

It includes the open-source runtime plus the enterprise packages that add:

- durable spill persistence during Redis outages
- reconciliation to replay persisted writes after Redis recovers
- enterprise feature gating through the dedicated `VapeCache.Licensing` package

If you only need the runtime, use the OSS repository:

- `https://github.com/haxxornulled/VapeCache`

## Why It Was Designed

VapeCache was designed around a simple constraint: cache traffic is not the same thing as general Redis traffic.

Most production applications do not just need a Redis client. They need a cache runtime that:

- keeps hot-path cache operations fast
- limits blast radius when Redis slows down or disconnects
- prevents stampedes on hot keys
- gives operators real telemetry about pressure, queueing, lanes, and failover state

This project exists to make those behaviors first-class instead of treating them as application glue around a generic Redis client.

## What You Actually Get

### Core Runtime

- cache-oriented Redis transport with multiplexing and ordered responses
- coalesced socket writes for better write-path efficiency
- transport guardrails and runtime option normalization
- RESP2/RESP3 negotiation and cluster MOVED/ASK handling on cache paths
- circuit breaker and hybrid in-memory failover when Redis is unhealthy
- stampede protection profiles for hot-key workloads
- typed cache APIs plus low-level byte-oriented APIs
- OpenTelemetry metrics and traces
- Aspire integration and ASP.NET Core integration

### Enterprise Additions

- durable spill persistence for fallback writes
- reconciliation pipeline to drain persisted writes back to Redis
- license validation and enterprise feature gates via `VapeCache.Licensing`
- control-plane support for activation and revocation workflows in the dedicated `VapeCache.Licensing` repository

### What It Is Not

- not a general-purpose Redis administration client
- not a promise that one benchmark result applies to every workload
- not a replacement for application-level data modeling

Benchmarks are included in this repo because transport regressions matter. Read them as workload-specific evidence, not universal claims.

## Package Map

| Package / Service | Purpose | In OSS |
|---|---|---|
| `VapeCache` | Core runtime, Redis transport, cache APIs, telemetry | Yes |
| `VapeCache.Abstractions` | Contracts, options, value types | Yes |
| `VapeCache.Extensions.Aspire` | Aspire wiring, endpoints, telemetry integration | Yes |
| `VapeCache.Extensions.AspNetCore` | ASP.NET Core output cache integration | No |
| `VapeCache.Persistence` | Durable spill persistence | No |
| `VapeCache.Reconciliation` | Replay persisted writes after recovery | No |
| `VapeCache.Licensing` | Enterprise license validation/runtime package (published from `haxxornulled/VapeCache.Licensing`) | No |
| `VapeCache.Licensing.ControlPlane` | Enterprise control-plane service (published from `haxxornulled/VapeCache.Licensing`) | No |

## Quick Start

### 1. Run Redis

```bash
docker run --name vapecache-redis -p 6379:6379 -d redis:7
```

### 2. Install the Core Package

```bash
dotnet add package VapeCache
```

### 3. Configure Redis

`appsettings.json`

```json
{
  "RedisConnection": {
    "Host": "localhost",
    "Port": 6379,
    "Database": 0
  }
}
```

### 4. Register Services

`Program.cs`

```csharp
using VapeCache.Abstractions.Connections;
using VapeCache.Abstractions.Caching;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Connections;

builder.Services.AddOptions<RedisConnectionOptions>()
    .Bind(builder.Configuration.GetSection("RedisConnection"));
builder.Services.AddVapecacheRedisConnections();
builder.Services.AddVapecacheCaching();
```

If you do not bind `RedisConnection` from configuration, set `VAPECACHE_REDIS_CONNECTIONSTRING` before first cache use.

### 5. Use the Cache API

```csharp
public sealed class PingService(ICacheService cache)
{
    public Task<string?> GetAsync(CancellationToken ct) =>
        cache.GetOrSetAsync(
            "demo:ping",
            _ => Task.FromResult("pong"),
            (writer, value) => JsonSerializer.Serialize(writer, value),
            bytes => JsonSerializer.Deserialize<string>(bytes),
            new CacheEntryOptions(Ttl: TimeSpan.FromMinutes(1)),
            ct);
}
```

## Documentation

Start here:

- [docs/INDEX.md](docs/INDEX.md)
- [docs/QUICKSTART.md](docs/QUICKSTART.md)
- [docs/CONFIGURATION.md](docs/CONFIGURATION.md)
- [docs/API_REFERENCE.md](docs/API_REFERENCE.md)
- [docs/ASPIRE_INTEGRATION.md](docs/ASPIRE_INTEGRATION.md)
- [docs/PERFORMANCE.md](docs/PERFORMANCE.md)
- [docs/UPGRADE_NOTES.md](docs/UPGRADE_NOTES.md)

## Build And Test

### Build

```bash
dotnet build VapeCache.sln -c Release
```

### Test

```bash
dotnet test VapeCache.Tests/VapeCache.Tests.csproj -c Release
dotnet test VapeCache.PerfGates.Tests/VapeCache.PerfGates.Tests.csproj -c Release
```

## License

Community use is licensed under [LICENSE.md](LICENSE.md) for non-commercial use.

Commercial use requires an enterprise license.
