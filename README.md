<p align="center">
  <img src="assets/vapecache-brand.png" alt="VapeCache logo" width="920" />
</p>

# VapeCache

VapeCache is a Redis-first cache runtime for .NET 10. It is built for applications that need fast cache paths, bounded behavior during Redis trouble, and production telemetry that explains what the cache layer is doing.

## What This Repo Is

This repository is the OSS runtime.

It contains the core Redis transport, cache APIs, telemetry, and framework integrations.

If you need durable spill persistence, reconciliation, or enterprise control-plane features, use the enterprise repository:

- `https://github.com/haxxornulled/VapeCache-Enterprise`

## Why It Was Designed

VapeCache was designed for cache-heavy systems where the main problems are:

- hot-path latency
- hot-key stampedes
- cascading failures when Redis becomes slow or unavailable
- poor visibility into queue pressure and transport behavior

The goal is not to be everything Redis can do. The goal is to make cache behavior predictable and observable in real .NET applications.

## What You Actually Get

- cache-oriented Redis transport with multiplexing and ordered responses
- coalesced socket writes on the write path
- runtime guardrails for transport settings
- RESP2/RESP3 negotiation and cluster MOVED/ASK handling on cache paths
- circuit breaker and hybrid in-memory failover
- stampede protection profiles
- typed cache APIs plus low-level byte-oriented APIs
- OpenTelemetry metrics and traces
- Aspire integration and ASP.NET Core integration

## What You Do Not Get In OSS

The OSS repo does not include:

- durable spill persistence
- write reconciliation after Redis recovery
- enterprise licensing and control-plane enforcement

Those live in the enterprise repo.

## Package Map

| Package | Purpose |
|---|---|
| `VapeCache` | Core runtime, Redis transport, cache APIs, telemetry |
| `VapeCache.Abstractions` | Contracts, options, value types |
| `VapeCache.Extensions.Aspire` | Aspire wiring, endpoints, telemetry integration |

## Quick Start

### 1. Run Redis

```bash
docker run --name vapecache-redis -p 6379:6379 -d redis:7
```

### 2. Install the Package

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
using VapeCache.Abstractions.Caching;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Connections;

builder.Services.AddVapecacheRedisConnections();
builder.Services.AddVapecacheCaching();
```

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

## Documentation Coverage

- Full options/defaults reference (generated from source): [docs/SETTINGS_REFERENCE.md](docs/SETTINGS_REFERENCE.md)
- Configuration deep dive: [docs/CONFIGURATION.md](docs/CONFIGURATION.md)
- Quick setup: [QUICK_START.md](QUICK_START.md)

## Documentation

Start here:

- [docs/INDEX.md](docs/INDEX.md)
- [docs/QUICKSTART.md](docs/QUICKSTART.md)
- [docs/CONFIGURATION.md](docs/CONFIGURATION.md)
- [docs/LOGGING_TELEMETRY_CONFIGURATION.md](docs/LOGGING_TELEMETRY_CONFIGURATION.md)
- [docs/API_REFERENCE.md](docs/API_REFERENCE.md)
- [docs/ASPIRE_INTEGRATION.md](docs/ASPIRE_INTEGRATION.md)
- [docs/PERFORMANCE.md](docs/PERFORMANCE.md)

## Build And Test

### Build

```bash
dotnet build VapeCache.sln -c Release
```

### Test

```bash
dotnet test VapeCache.Tests/VapeCache.Tests.csproj -c Release
```

## License

Community use is licensed under [LICENSE](LICENSE) (PolyForm Noncommercial 1.0.0 with required notices).

Commercial use requires an enterprise license from the enterprise repository.

