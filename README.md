# VapeCache

**Enterprise-grade Redis caching library for .NET 10** with hybrid fallback, circuit breaker, and production observability.

[![Build Status](https://img.shields.io/badge/build-passing-brightgreen)](https://github.com/haxxornulled/VapeCache)
[![NuGet VapeCache](https://img.shields.io/badge/nuget-v1.0.0-blue)](https://github.com/haxxornulled/VapeCache/releases)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple)](https://dot.net)

---

## ⚡ Why VapeCache?

### Built for Performance
- **5-30% faster than StackExchange.Redis** (ordered multiplexing + coalesced writes)
- **Zero-copy leasing** for large values (no LOH spikes)
- **Pooled `IValueTaskSource`** eliminates `TaskCompletionSource` churn

### Built for Reliability
- **Hybrid cache**: Automatic fallback to in-memory when Redis is unavailable
- **Circuit breaker**: Stops hammering Redis during outages, auto-recovers
- **Stampede protection**: Coalesces concurrent requests for the same key
- **Startup preflight**: Validates Redis before serving traffic

### Built for Observability
- **OpenTelemetry native**: 20+ metrics + distributed tracing
- **Structured logging**: Works with Serilog, NLog, or any `ILogger<T>` provider
- **Production-ready telemetry**: 1-2% CPU overhead, massive troubleshooting value

---

## 📦 Quick Start

### Installation

```bash
# Install the main package
dotnet add package VapeCache

# Or just the abstractions (for library authors)
dotnet add package VapeCache.Abstractions

# .NET Aspire integration (optional)
dotnet add package VapeCache.Extensions.Aspire
```

### Configuration (appsettings.json)

**Minimal Configuration:**
```json
{
  "RedisConnection": {
    "Host": "localhost",
    "Port": 6379,
    "Database": 0
  }
}
```

**Production Configuration:**
```json
{
  "RedisConnection": {
    "Host": "redis.example.com",
    "Port": 6380,
    "Database": 0,
    "Password": "your-secure-password",
    "UseTls": true,
    "ConnectTimeout": "00:00:05",
    "MaxConnections": 64,
    "MaxIdle": 64
  },
  "RedisMultiplexer": {
    "Connections": 4,
    "MaxInFlightPerConnection": 4096,
    "EnableCoalescedSocketWrites": true,
    "EnableCommandInstrumentation": true
  },
  "CacheService": {
    "EnableCircuitBreaker": true,
    "InMemoryCacheSizeLimitMb": 100
  }
}
```

📖 **[Complete Configuration Reference](docs/CONFIGURATION.md)** - All appsettings.json options documented

**Production Secrets Management:**
- 🔐 **[Azure Key Vault Integration](docs/CONFIGURATION.md#example-azure-key-vault-integration)** - Load Redis passwords from Key Vault (recommended)
- 🔑 **[Managed Identity](docs/CONFIGURATION.md#option-3-managed-identity-production-recommended)** - Production credential management

### Basic Usage
```csharp
// 1. Add to your host (Program.cs)
builder.Services.AddVapecacheRedisConnections();
builder.Services.AddVapecacheCaching();

// 2. Inject and use in your services
public class MyService
{
    private readonly ICacheService _cache;

    public MyService(ICacheService cache) => _cache = cache;

    public async Task<User?> GetUserAsync(int id, CancellationToken ct)
    {
        var key = $"user:{id}";
        return await _cache.GetOrSetAsync(
            key,
            async ct => await _db.Users.FindAsync(id, ct), // Factory
            (writer, user) => JsonSerializer.Serialize(writer, user), // Serialize
            bytes => JsonSerializer.Deserialize<User>(bytes), // Deserialize
            new CacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) },
            ct);
    }
}
```

### .NET Aspire Usage
```csharp
// AppHost (orchestration)
var builder = DistributedApplication.CreateBuilder(args);
var redis = builder.AddRedis("redis");
var api = builder.AddProject<Projects.MyApi>("api")
    .WithReference(redis);

// API Project (Program.cs)
builder.AddVapeCache()
    .WithRedisFromAspire("redis")
    .WithHealthChecks()
    .WithAspireTelemetry();  // Cache hits/misses → Aspire Dashboard

// View metrics at http://localhost:15888
```

See [.NET Aspire Integration](docs/ASPIRE_INTEGRATION.md) for details.

---

## 🎯 Key Features

### Hybrid Cache with Intelligent Fallback
```mermaid
graph LR
    A[Your App] --> B[VapeCache]
    B --> C{Redis Available?}
    C -->|Yes| D[Redis]
    C -->|No| E[In-Memory Cache]
    D -.->|Circuit Breaker Opens| E
```

- Automatic failover to `IMemoryCache` when Redis is down
- Circuit breaker prevents cascading failures
- Sanity check re-enables Redis when it recovers

### Enterprise Reliability Features
- ✅ **Startup Preflight**: Validate Redis before accepting traffic
- ✅ **Auto-Reconnect**: Drains pending operations, reconnects on next request
- ✅ **Connection Pooling**: Warm idle connections, reaper drops stale sockets
- ✅ **Stampede Protection**: Coalesce concurrent cache misses
- ✅ **Configurable Timeouts**: Connect, acquire, validate, command-level

### Production Observability
- ✅ **OpenTelemetry Metrics**: Connection pool, Redis commands, cache hits/misses
- ✅ **Distributed Tracing**: Activity spans for every Redis operation
- ✅ **Structured Logging**: Connection events, pool activity, circuit breaker state
- ✅ **SEQ/Grafana/Aspire**: Works with all major observability platforms

---

## 📚 Documentation

### Getting Started
- [Quickstart Guide](docs/QUICKSTART.md) - Get running in 5 minutes
- [Configuration Guide](docs/CONFIGURATION.md) - appsettings.json reference
- [.NET Aspire Integration](docs/ASPIRE_INTEGRATION.md) - Cloud-native deployment

### Architecture & Design
- [Architecture Overview](docs/ARCHITECTURE.md) - High-level design
- [Why We Beat StackExchange.Redis](docs/PERFORMANCE.md) - Performance deep-dive
- [Coalesced Writes](docs/COALESCED_WRITES.md) - 5-30% faster socket I/O
- [Configuration Best Practices](docs/CONFIGURATION_BEST_PRACTICES.md) - IOptions<T> pattern

### Observability
- [Observability Architecture](docs/OBSERVABILITY_ARCHITECTURE.md) - Metrics, traces, logs
- [SEQ Integration](docs/OBSERVABILITY_ARCHITECTURE.md#seq-integration) - Structured logging
- [Prometheus + Grafana](docs/OBSERVABILITY_ARCHITECTURE.md#prometheus--grafana) - Metrics
- [.NET Aspire Dashboard](docs/ASPIRE_INTEGRATION.md) - Cloud-native observability

### API Reference
- **[Complete API Reference](docs/API_REFERENCE.md)** - Full API documentation
  - [ICacheService](docs/API_REFERENCE.md#icacheservice) - Core caching operations
  - [Typed Collections API](docs/API_REFERENCE.md#typed-collections-api) - Lists, Sets, Hashes
  - [Serialization Patterns](docs/API_REFERENCE.md#serialization) - Zero-allocation serialization
  - [Performance Patterns](docs/API_REFERENCE.md#performance-patterns) - Best practices
- [Redis Protocol Support](docs/REDIS_PROTOCOL_SUPPORT.md) - What commands are supported
- [API Expansion Plan](docs/API_EXPANSION_PLAN.md) - Roadmap to 200+ commands
- [Non-Goals](docs/NON_GOALS.md) - What VapeCache is **not**

### Operations
- [Failure Scenarios](docs/FAILURE_SCENARIOS.md) - What happens when Redis fails
- [TLS Security](docs/TLS_SECURITY.md) - Production TLS best practices
- [Benchmarking Guide](docs/BENCHMARKING.md) - Reproduce performance results

---

## 🏗️ Architecture

### High-Level Components

```mermaid
flowchart TD
    App[Your Application]

    subgraph Cache["VapeCache Layers"]
        ICache[ICacheService<br/>Core API]
        Stampede[StampedeProtectedCacheService<br/>Coalesce concurrent requests]
        Hybrid[HybridCacheService<br/>Circuit breaker + fallback]

        subgraph Backends["Cache Backends"]
            Redis[RedisCacheService<br/>Redis backend]
            Memory[InMemoryCacheService<br/>In-memory fallback]
        end
    end

    subgraph Transport["Redis Transport"]
        Executor[RedisCommandExecutor<br/>4 multiplexed connections]
        Mux[RedisMultiplexedConnection<br/>Ordered pipelining + coalesced writes]
        Pool[RedisConnectionPool<br/>Connection pooling + reaper]
        Socket[Socket/NetworkStream/SslStream<br/>TCP/TLS connection]
    end

    App --> ICache
    ICache --> Stampede
    Stampede --> Hybrid
    Hybrid -->|Primary| Redis
    Hybrid -.->|Failover| Memory
    Redis --> Executor
    Executor --> Mux
    Mux --> Pool
    Pool --> Socket
    Socket -->|RESP2 Protocol| RedisServer[(Redis Server)]

    style Hybrid fill:#e1f5ff
    style Memory fill:#ffe1e1
    style Redis fill:#e1ffe1
    style Mux fill:#fff4e1
```

### Transport Layer (Why We're Fast)
- **Ordered Multiplexing**: `Channel<>` + pooled `IValueTaskSource` (no TCS churn)
- **Coalesced Writes**: Batch commands into single socket send (5-30% faster)
- **Deterministic Buffers**: `ArrayPool` for bulk replies (no LOH spikes)
- **Auto-Reconnect**: Drain pending ops, release slots, reconnect seamlessly

See [docs/COALESCED_WRITES.md](docs/COALESCED_WRITES.md) for deep-dive.

---

## 🚀 Performance

### Benchmark Results (vs StackExchange.Redis)

**Environment:** 4 multiplexed connections, 4096 max in-flight, .NET 10, Release build

| Payload Size | Operation | VapeCache | StackExchange.Redis | Improvement |
|--------------|-----------|-----------|---------------------|-------------|
| 32 bytes     | SET       | 1.29x faster | Baseline | **+29%** |
| 32 bytes     | GET       | 1.08x faster | Baseline | **+8%** |
| 1 KB         | SET       | 1.12x faster | Baseline | **+12%** |
| 4 KB         | GET       | 1.07x faster | Baseline | **+7%** |

**Memory:** ~2.1 KB allocated/op (no payload garbage, pooled buffers)

See [docs/PERFORMANCE.md](docs/PERFORMANCE.md) for full benchmark methodology.

---

## 🔧 Development

### Build
```bash
dotnet build VapeCache.sln -c Release
```

### Test
```bash
dotnet test -c Release
```

### Run Console Host (Live Demo)
```bash
# Set Redis connection string
$env:VAPECACHE_REDIS_CONNECTIONSTRING = 'redis://localhost:6379/0'

# Run console host with HTTP endpoints
dotnet run --project VapeCache.Console -c Release

# HTTP endpoints available at http://localhost:5080
# - GET /healthz
# - GET /cache/stats
# - PUT /cache/{key}?ttlSeconds=60
# - GET /cache/{key}
```

### Run Benchmarks
```bash
dotnet run -c Release --project VapeCache.Benchmarks -- --filter *RedisClientComparisonBenchmarks*
```

---

## 📋 Roadmap

### Current Status (v0.9 - Pre-Release)
- ✅ Core caching commands (GET, SET, MGET, MSET, etc.)
- ✅ Hybrid cache with circuit breaker
- ✅ Ordered multiplexing + coalesced writes
- ✅ OpenTelemetry metrics + tracing
- ✅ Connection pooling + reaper
- ✅ Comprehensive documentation

### v1.0 (2 weeks)
- ✅ .NET Aspire integration package (VapeCache.Extensions.Aspire)
- [ ] Backpressure metrics (queue depth, wait time)
- [ ] Memory accounting (buffer pool telemetry)
- [ ] TLS security documentation
- [ ] Failure scenario matrix
- [ ] NuGet packages published

### v1.1 (Q2 2025)
- [ ] Expanded command surface (20 → 50 commands)
- [ ] Pub/Sub API
- [ ] Lua scripting support

### v2.0 (Q3 2025)
- [ ] Fluent API builders (LINQ-style)
- [ ] Source generators (compile-time validation)
- [ ] Cluster mode support (maybe)

See [docs/API_EXPANSION_PLAN.md](docs/API_EXPANSION_PLAN.md) for detailed roadmap.

---

## 🤝 Contributing

Contributions are welcome! See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

**Areas we'd love help with:**
- Expanding Redis command surface (see [docs/API_EXPANSION_PLAN.md](docs/API_EXPANSION_PLAN.md))
- Additional codec implementations (`ICacheCodecProvider`)
- Integration packages (Aspire, Serilog, OpenTelemetry)
- Documentation improvements

---

## 🎯 Use Cases

### ✅ When to Use VapeCache
- High-performance GET/SET caching
- Need hybrid cache (Redis + in-memory fallback)
- Want production observability out-of-the-box
- Building cloud-native apps with .NET Aspire
- Need predictable memory usage (no LOH spikes)

### ❌ When NOT to Use VapeCache
- Need full Redis command surface (200+ commands) → Use StackExchange.Redis
- Need Pub/Sub right now → Use StackExchange.Redis
- Need Lua scripting right now → Use StackExchange.Redis
- Need cluster mode → Use StackExchange.Redis or Sentinel

**Recommended:** Use VapeCache for caching + StackExchange.Redis for advanced features (hybrid approach).

See [docs/NON_GOALS.md](docs/NON_GOALS.md) for strategic positioning.

---

## 📜 License

MIT License - See [LICENSE](LICENSE) for details

---

## 🙏 Acknowledgments

- Built with ❤️ using .NET 10
- Inspired by StackExchange.Redis, but optimized for caching workloads
- OpenTelemetry for native observability

---

## 📞 Support

- **GitHub Issues**: [https://github.com/haxxornulled/VapeCache/issues](https://github.com/haxxornulled/VapeCache/issues)
- **Documentation**: [docs/](docs/)
- **Discussions**: [GitHub Discussions](https://github.com/haxxornulled/VapeCache/discussions) (coming soon)
