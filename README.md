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

## 💰 Pricing & Licensing

VapeCache uses an **Open Core** model to maximize community adoption while offering enterprise-grade paid features.

### Free Tier (MIT License) ✅

**Core packages are 100% free and open source:**
- VapeCache (core library)
- VapeCache.Abstractions
- VapeCache.Infrastructure
- VapeCache.Extensions.Aspire

**Features:**
- ✅ Full Redis caching API
- ✅ Connection pooling & multiplexing
- ✅ Circuit breaker (basic, no persistence)
- ✅ Stampede protection
- ✅ OpenTelemetry metrics & tracing
- ✅ 5-30% faster than StackExchange.Redis

### Pro Tier - $99/month 💎

**Perfect for startups and small teams (max 5 production instances)**

**Premium Packages:**
- VapeCache.Modules (Redis Bloom, Search, TimeSeries, JSON)
- VapeCache.Pro.Telemetry (Advanced metrics & health checks)

**Additional Features:**
- ✅ Redis module support (Bloom filters, Search, TimeSeries, JSON)
- ✅ Advanced telemetry & distributed tracing
- ✅ Production health checks & diagnostics
- ✅ Priority email support (24h SLA)
- ✅ Community Slack access

[**Start Pro Trial →**](https://vapecache.com/pricing)

### Enterprise Tier - $499/month 🏢

**For Fortune 500 and regulated industries (unlimited instances)**

**Everything in Pro, plus:**
- ✅ **ZERO DATA LOSS RECONCILIATION** (SQLite-backed persistence)
- ✅ Unlimited production instances
- ✅ Multi-region replication
- ✅ Compliance suite (GDPR/HIPAA audit logs, encryption at rest)
- ✅ Cloud optimizations (Azure, AWS, GCP)
- ✅ 24/7 support (4h SLA)
- ✅ Source code access
- ✅ Quarterly architecture reviews

[**Contact Sales →**](https://vapecache.com/enterprise)

---

**License Keys:**

To use Enterprise features (reconciliation), set your license key as an environment variable or pass it during registration:

```bash
# Environment variable
export VAPECACHE_LICENSE_KEY="VCENT-CUST12345-1735689600-999-A1B2C3D4E5F6G7H8"
```

```csharp
// Or pass directly to reconciliation
builder.Services.AddVapeCacheRedisReconciliation("VCENT-...");
```

Trial keys available at [vapecache.com/trial](https://vapecache.com/trial)

---

## 📦 Quick Start

### Installation

```bash
# Install the main package
dotnet add package VapeCache

# Or just the abstractions (for library authors)
dotnet add package VapeCache.Abstractions

# Redis reconciliation (COMMERCIAL - requires Enterprise license)
dotnet add package VapeCache.Reconciliation

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
    "ResponseTimeout": "00:00:02",
    "EnableCoalescedSocketWrites": true,
    "EnableCommandInstrumentation": true
  },
  "RedisCircuitBreaker": {
    "Enabled": true,
    "ConsecutiveFailuresToOpen": 2,
    "BreakDuration": "00:00:10",
    "HalfOpenProbeTimeout": "00:00:00.250"
  },
  "CacheStampede": {
    "Enabled": true,
    "MaxKeys": 100000
  }
}
```

📖 **[Complete Configuration Reference](docs/CONFIGURATION.md)** - All appsettings.json options documented

### Redis Reconciliation (Enterprise Feature)

**⚠️ Requires Enterprise license** - Contact [vapecache.com/enterprise](https://vapecache.com/enterprise)

```csharp
// Pass your Enterprise license key (or set VAPECACHE_LICENSE_KEY environment variable)
builder.Services.AddVapeCacheRedisReconciliation(
    licenseKey: "VCENT-CUST12345-...",
    configure: options =>
    {
        options.MaxOperationAge = TimeSpan.FromMinutes(5);
    });
```

```json
{
  "RedisReconciliation": {
    "Enabled": true,
    "MaxOperationAge": "00:05:00"
  },
  "RedisReconciliationStore": {
    "UseSqlite": true,
    "StorePath": "%LOCALAPPDATA%/VapeCache/persistence/reconciliation.db"
  }
}
```

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
            new CacheEntryOptions(Ttl: TimeSpan.FromMinutes(5)),
            ct);
    }
}
```

### Autofac Registration
```csharp
builder.RegisterModule(new VapeCache.Infrastructure.DependencyInjection.VapeCacheConnectionsModule());
builder.RegisterModule(new VapeCache.Infrastructure.DependencyInjection.VapeCacheCachingModule());
```

### Fast-Fail List Pops (Try*)
```csharp
var workQueue = collections.List<WorkItem>("jobs:pending");

if (!workQueue.TryPopFrontAsync(ct, out var task))
{
    // Multiplexer saturated: skip or backoff instead of waiting
    return;
}

var workItem = await task;
if (workItem is not null)
{
    await ProcessAsync(workItem);
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

Start here: [Docs Index](docs/INDEX.md)

### Getting Started
- [Quickstart Guide](docs/QUICKSTART.md)
- [Configuration Guide](docs/CONFIGURATION.md)
- [.NET Aspire Integration](docs/ASPIRE_INTEGRATION.md)

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
- [API Reference](docs/API_REFERENCE.md)
- [Redis Protocol Support](docs/REDIS_PROTOCOL_SUPPORT.md)
- [API Expansion Backlog](docs/API_EXPANSION_PLAN.md)
- [Non-Goals](docs/NON_GOALS.md)
- [FAQ](docs/FAQ.md)

### Operations
- [Failure Scenarios](docs/FAILURE_SCENARIOS.md) - What happens when Redis fails
- [TLS Security](docs/TLS_SECURITY.md) - Production TLS best practices
- [Benchmarking Guide](docs/BENCHMARKING.md) - Reproduce performance results

---

## 🏗️ Architecture

### High-Level Components

```mermaid
flowchart TB
    App["🖥️ Your Application"]

    subgraph VapeCache["VapeCache - Caching Layer"]
        direction TB
        API["ICacheService<br/><i>Public API</i>"]
        Stampede["StampedeProtectedCacheService<br/><i>Coalesce concurrent cache misses</i>"]
        Hybrid["HybridCacheService<br/><i>Circuit breaker + automatic failover</i>"]

        subgraph Backends["Backend Implementations"]
            direction LR
            RedisBackend["RedisCacheService<br/><i>Primary: Redis backend</i>"]
            MemoryBackend["InMemoryCacheService<br/><i>Fallback: In-memory cache</i>"]
        end
    end

    subgraph RedisTransport["Redis Transport Layer - Why We're Fast"]
        direction TB
        Executor["RedisCommandExecutor<br/><i>4 multiplexed connections</i>"]
        Multiplexer["RedisMultiplexedConnection<br/><i>Ordered pipelining + coalesced writes (29% faster)</i>"]
        ConnectionPool["RedisConnectionPool<br/><i>Connection pooling + idle reaper</i>"]
        Network["Socket / NetworkStream / SslStream<br/><i>TCP or TLS connection</i>"]
    end

    RedisInstance[("⚡ Redis Server<br/><i>RESP2 Protocol</i>")]

    App --> API
    API --> Stampede
    Stampede --> Hybrid
    Hybrid -->|"✓ Healthy"| RedisBackend
    Hybrid -.->|"⚠️ Circuit Open"| MemoryBackend
    RedisBackend --> Executor
    Executor --> Multiplexer
    Multiplexer --> ConnectionPool
    ConnectionPool --> Network
    Network -->|"RESP2 Commands"| RedisInstance

    style Hybrid fill:#e1f5ff,stroke:#0066cc,stroke-width:3px
    style MemoryBackend fill:#ffe1e1,stroke:#cc0000,stroke-width:2px
    style RedisBackend fill:#e1ffe1,stroke:#00cc00,stroke-width:3px
    style Multiplexer fill:#fff4e1,stroke:#ff9900,stroke-width:3px
    style RedisInstance fill:#ffcccc,stroke:#cc0000,stroke-width:3px
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

dotnet run --project VapeCache.Console -c Release
```
Console host runs the demo workloads and logs cache activity; it does not expose HTTP endpoints.

### Run Benchmarks
```bash
$env:VAPECACHE_REDIS_CONNECTIONSTRING = "redis://localhost:6379/0"
dotnet run -c Release --project VapeCache.Benchmarks -- --filter *RedisClientStackExchangeBenchmarks*
dotnet run -c Release --project VapeCache.Benchmarks -- --filter *RedisClientVapeCacheBenchmarks*
```

---

## 📋 Roadmap

### Current (v1.0)
- ✅ Core caching commands and typed collections (List/Set/Hash/SortedSet)
- ✅ Hybrid cache with circuit breaker + reconciliation (optional)
- ✅ Ordered multiplexing + coalesced writes
- ✅ OpenTelemetry metrics + tracing
- ✅ Redis module commands (RedisJSON, RediSearch, RedisBloom, RedisTimeSeries)
- ✅ .NET Aspire integration package

### Backlog (Scoped)
- [ ] Expand core command surface (INCR/DECR, EXISTS, etc.)
- [ ] Backpressure metrics (queue depth, wait time)
- [ ] Buffer pool accounting telemetry
- [ ] Additional codec implementations

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
- **Documentation**: [docs/INDEX.md](docs/INDEX.md)
- **Discussions**: [GitHub Discussions](https://github.com/haxxornulled/VapeCache/discussions) (coming soon)
