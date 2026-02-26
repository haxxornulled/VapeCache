<p align="center">
  <img src="assets/vapecache-brand.png" alt="VapeCache logo" width="920" />
</p>

# VapeCache

**Enterprise-grade Redis caching library for .NET 10** with hybrid fallback, circuit breaker, and production observability.

[![Build Status](https://img.shields.io/badge/build-passing-brightgreen)](https://github.com/haxxornulled/VapeCache)
[![NuGet VapeCache](https://img.shields.io/badge/nuget-v1.0.1-blue)](https://github.com/haxxornulled/VapeCache/releases)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple)](https://dot.net)

---

## 🚀 TL;DR Quick Start (For Impatient Humans)

If you just want it running now, do this in order.
If you want the slower walkthrough for newer devs, use [docs/QUICKSTART.md](docs/QUICKSTART.md).

### 1. Run Redis

```bash
docker run --name vapecache-redis -p 6379:6379 -d redis:7
```

### 2. Install Package

```bash
dotnet add package VapeCache
```

### 3. Add Minimal Config (`appsettings.json`)

```json
{
  "RedisConnection": {
    "Host": "localhost",
    "Port": 6379,
    "Database": 0
  }
}
```

Optional for Redis Cluster + RESP3:

```json
{
  "RedisConnection": {
    "RespProtocolVersion": 3,
    "EnableClusterRedirection": true,
    "MaxClusterRedirects": 3
  }
}
```

### 4. Register VapeCache (`Program.cs`)

```csharp
using VapeCache.Abstractions.Caching;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Connections;

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

For the fluent "wire it all" path:

```csharp
builder.AddVapeCache()
    .WithRedisFromAspire("redis")
    .WithHealthChecks()
    .WithAspireTelemetry()
    .WithCacheStampedeProfile(CacheStampedeProfile.Balanced)
    .WithAutoMappedEndpoints();

var app = builder.Build();
app.MapHealthChecks("/health");
```

### 5. Use It

```csharp
public sealed class PingService(ICacheService cache)
{
    public Task<string?> GetAsync(CancellationToken ct) =>
        cache.GetOrSetAsync(
            "demo:ping",
            _ => Task.FromResult("pong"),
            (writer, value) => JsonSerializer.Serialize(writer, value),
            bytes => JsonSerializer.Deserialize<string>(bytes),
            new CacheEntryOptions(
                Ttl: TimeSpan.FromMinutes(1),
                Intent: new CacheIntent(CacheIntentKind.ReadThrough, Reason: "health ping")),
            ct);
}
```

### 6. ASP.NET Core Pipeline Hook (MVC/Blazor/Minimal API)

```bash
dotnet add package VapeCache.Extensions.AspNetCore
```

```csharp
builder.Services.AddVapeCacheOutputCaching(options =>
{
    options.AddBasePolicy(policy => policy.Expire(TimeSpan.FromSeconds(30)));
});

builder.Services.AddVapeCacheFailoverAffinityHints();

var app = builder.Build();
app.UseVapeCacheOutputCaching();
app.UseVapeCacheFailoverAffinityHints();
```

### 7. Stream Large Payloads (Chunked + Failover)

```csharp
app.MapGet("/media/{id}", async (string id, HttpResponse response, ICacheChunkStreamService streams, CancellationToken ct) =>
{
    var manifest = await streams.GetManifestAsync($"media:{id}", ct);
    if (manifest is null)
        return Results.NotFound();

    if (!string.IsNullOrWhiteSpace(manifest.Value.ContentType))
        response.ContentType = manifest.Value.ContentType;

    var copied = await streams.CopyToAsync($"media:{id}", response.Body, ct);
    return copied ? Results.Empty : Results.NotFound();
});
```

This uses the same hybrid backend as normal cache operations, so reads can continue from in-memory fallback if Redis is unavailable.

Need the full setup and tuning knobs? Jump to [📦 Quick Start](#-quick-start) below.

---

## ⚡ Why VapeCache Over StackExchange.Redis?

VapeCache is a **from-scratch Redis client** tuned for **cache-heavy workloads** with measurable performance and reliability wins.

### 🚀 Performance

### Enterprise Benchmarking Standard

Performance claims are validated with:
- fixed environment and Redis target
- Release-only builds
- repeated runs (median-of-N)
- fair and realworld mode comparisons
- allocation and tail-latency analysis alongside throughput

### Quick Commands

```powershell
$env:VAPECACHE_REDIS_CONNECTIONSTRING = "redis://localhost:6379/0"
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Job Short -Mode fair
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Job Short -Mode realworld
```

Payload scaling pass (client string set/get):

```powershell
$env:VAPECACHE_BENCH_CLIENT_OPERATIONS = "StringSetGet"
$env:VAPECACHE_BENCH_CLIENT_PAYLOADS = "1024,4096,16384"
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Suite client -Job Short -Mode fair
```

See [docs/PERFORMANCE.md](docs/PERFORMANCE.md) for interpretation and [docs/BENCHMARKING.md](docs/BENCHMARKING.md) for full runbook.
Current checked-in baseline snapshot: [docs/BENCHMARK_RESULTS.md](docs/BENCHMARK_RESULTS.md).

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
Console host runs demo workloads and logs cache activity (including GroceryStore dogfood and plugin examples). For HTTP wrappers, endpoints can be auto-mapped via `WithAutoMappedEndpoints(...)`.

### Run Benchmarks
```bash
$env:VAPECACHE_REDIS_CONNECTIONSTRING = "redis://localhost:6379/0"
dotnet run -c Release --project VapeCache.Benchmarks -- --filter *RedisClientHeadToHeadBenchmarks*
dotnet run -c Release --project VapeCache.Benchmarks -- --filter *RedisEndToEndHeadToHeadBenchmarks*
dotnet run -c Release --project VapeCache.Benchmarks -- --filter *RedisModuleHeadToHeadBenchmarks*

# Or run all head-to-head suites in fair mode (instrumentation off):
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Job Short -Mode fair

# Run the same suites with packet capture + Wireshark summaries:
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-with-capture.ps1 -Job Short -ConnectionString "redis://localhost:6379/0" -Interface 1 -RedisPort 6379
```

### Analyzer + Profiling Workflow
```bash
powershell -ExecutionPolicy Bypass -File tools/run-dotnet10-analysis.ps1 -Configuration Release -TreatWarningsAsErrors -VerifyFormatting -RunTests
powershell -ExecutionPolicy Bypass -File tools/profile-dotnet10.ps1 -Project "VapeCache.Console/VapeCache.Console.csproj" -Mode both -DurationSeconds 45 -Configuration Release
```

### GroceryStore Dogfood
```bash
powershell -ExecutionPolicy Bypass -File VapeCache.Console/run-grocery-dogfood.ps1 -ConnectionString "redis://localhost:6379/0" -ConcurrentShoppers 200 -TotalShoppers 5000 -TargetDurationSeconds 30 -Profile FullTilt -EnablePluginDemo
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

## 🔐 Enterprise Licensing Ops

- Online revocation/kill-switch service: `VapeCache.Licensing.ControlPlane`
- Runtime operations runbook: [docs/LICENSE_OPERATIONS_RUNBOOK.md](docs/LICENSE_OPERATIONS_RUNBOOK.md)
- Control-plane setup guide: [docs/LICENSE_CONTROL_PLANE.md](docs/LICENSE_CONTROL_PLANE.md)
- Issuer externalization plan: [docs/LICENSE_GENERATOR_EXTERNALIZATION.md](docs/LICENSE_GENERATOR_EXTERNALIZATION.md)

---

## 🎯 Use Cases

### ✅ When to Use VapeCache
- High-performance GET/SET caching
- Need hybrid cache (Redis + in-memory fallback)
- Want production observability out-of-the-box
- Building cloud-native apps with .NET Aspire
- Need predictable memory usage (no LOH spikes)

### ❌ When NOT to Use VapeCache
- Need full Redis command surface (200+ commands) → VapeCache focuses on caching use cases
- Need Pub/Sub → Not currently supported (see roadmap)
- Need Lua scripting → Not currently supported (see roadmap)
- Need full cross-slot cluster orchestration across every Redis command → use StackExchange.Redis for now

See [docs/NON_GOALS.md](docs/NON_GOALS.md) for strategic positioning.

---

## 📜 License

MIT License - See [LICENSE](LICENSE) for details

---

## 🙏 Acknowledgments

- Built with ❤️ using .NET 10
- Original architecture designed for high-performance caching workloads
- OpenTelemetry for native observability

---

## 📞 Support

- **GitHub Issues**: [https://github.com/haxxornulled/VapeCache/issues](https://github.com/haxxornulled/VapeCache/issues)
- **Documentation**: [docs/INDEX.md](docs/INDEX.md)
- **Discussions**: [GitHub Discussions](https://github.com/haxxornulled/VapeCache/discussions) (coming soon)

