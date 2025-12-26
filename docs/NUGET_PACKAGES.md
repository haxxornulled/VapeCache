# VapeCache NuGet Package Architecture

## Overview

VapeCache is designed with a **separation of concerns** architecture where the core caching library is agnostic to logging implementations and cloud deployment platforms. Users choose their observability stack (Serilog, NLog, etc.) and deployment platform (.NET Aspire, Kubernetes, etc.) via optional extension packages.

## Package Dependency Hierarchy

```
┌──────────────────────────────────────────────────────────────────┐
│ Tier 1: Core Abstractions (Zero Dependencies)                    │
├──────────────────────────────────────────────────────────────────┤
│ VapeCache.Abstractions                                           │
│   - Public interfaces: ICacheService, IRedisConnection, etc.     │
│   - Value types: CacheKey, CacheEntryOptions                     │
│   - No logging, no telemetry, no external dependencies          │
│   - Pure contracts for maximum compatibility                     │
└──────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────┐
│ Tier 2: Core Implementation (Minimal Dependencies)               │
├──────────────────────────────────────────────────────────────────┤
│ VapeCache.Infrastructure                                         │
│   - Redis RESP protocol implementation                           │
│   - Connection pooling + multiplexing                            │
│   - Hybrid cache (Redis + in-memory fallback)                   │
│   - Circuit breaker + stampede protection                        │
│                                                                   │
│   Dependencies:                                                   │
│   ✓ Microsoft.Extensions.Logging.Abstractions (ILogger<T>)      │
│   ✓ Microsoft.Extensions.Caching.Memory (IMemoryCache)          │
│   ✓ System.Diagnostics.DiagnosticSource (Activity, Meter)       │
│   ✗ NO Serilog sinks (removed in favor of ILogger abstraction)  │
│   ✗ NO Seq/console/file logging (belongs in host project)       │
└──────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────┐
│ Tier 3: Integration Extensions (Optional, User Choice)           │
├──────────────────────────────────────────────────────────────────┤
│ VapeCache.Extensions.Aspire (NEW - PLANNED)                     │
│   Purpose: .NET Aspire integration for cloud-native apps        │
│   Features:                                                       │
│     - AddVapeCache() for IHostApplicationBuilder                │
│     - Automatic service discovery (Redis resource binding)       │
│     - Built-in health checks (/health/redis, /health/cache)     │
│     - Aspire dashboard telemetry (auto-wired OTel)              │
│     - Configuration defaults optimized for containers            │
│   Dependencies:                                                   │
│     - Aspire.Hosting.Redis                                       │
│     - Microsoft.Extensions.ServiceDiscovery                      │
│   Usage:                                                          │
│     builder.AddVapeCache()                                       │
│            .WithRedisFromAspire("redis")                         │
│            .WithHealthChecks()                                   │
│            .WithAspireTelemetry();                               │
│                                                                   │
│ VapeCache.Extensions.Serilog (NEW - PLANNED)                    │
│   Purpose: Optional Serilog enrichers for VapeCache             │
│   Features:                                                       │
│     - VapeCache-specific log enrichers (ConnectionId, etc.)     │
│     - Pre-configured SEQ sink helpers (optional)                │
│     - Trace correlation helpers                                 │
│   Dependencies:                                                   │
│     - Serilog                                                    │
│     - Serilog.Enrichers.Span                                    │
│   Usage:                                                          │
│     Log.Logger = new LoggerConfiguration()                       │
│         .Enrich.WithVapeCacheContext()                           │
│         .WriteTo.Seq("http://localhost:5341")                    │
│         .CreateLogger();                                         │
│                                                                   │
│ VapeCache.Extensions.OpenTelemetry (NEW - PLANNED)              │
│   Purpose: Pre-configured OpenTelemetry helpers                  │
│   Features:                                                       │
│     - AddVapeCacheMetrics() extension                           │
│     - AddVapeCacheTracing() extension                           │
│     - Pre-configured exporters (OTLP, Prometheus, Zipkin)       │
│   Dependencies:                                                   │
│     - OpenTelemetry.Extensions.Hosting                          │
│     - OpenTelemetry.Exporter.Prometheus.AspNetCore              │
│   Usage:                                                          │
│     builder.Services.AddOpenTelemetry()                          │
│         .WithMetrics(m => m.AddVapeCacheMetrics())              │
│         .WithTracing(t => t.AddVapeCacheTracing());             │
└──────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────┐
│ Tier 4: Host Projects (Not Published to NuGet)                   │
├──────────────────────────────────────────────────────────────────┤
│ VapeCache.Console                                                │
│   - Example host with Serilog + SEQ configuration               │
│   - Stress testing tools                                         │
│   - HTTP endpoints for Postman testing                          │
│   - NOT published to NuGet (local development only)             │
│                                                                   │
│ VapeCache.Tests                                                  │
│   - Unit and integration tests                                   │
│   - NOT published to NuGet                                       │
│                                                                   │
│ VapeCache.Benchmarks                                             │
│   - BenchmarkDotNet performance tests                            │
│   - NOT published to NuGet                                       │
└──────────────────────────────────────────────────────────────────┘
```

## Design Principles

### 1. **Library Agnostic Logging**
- **Core libraries use `ILogger<T>` ONLY**
  - VapeCache.Infrastructure depends on `Microsoft.Extensions.Logging.Abstractions`
  - No direct Serilog/NLog/etc. dependencies in core packages
  - Users choose their logging implementation in the host project

### 2. **Observability by Default, Implementation by Choice**
- **Metrics/Traces via OpenTelemetry primitives**
  - `ActivitySource` and `Meter` are .NET BCL types (zero dependencies)
  - Exporters (Prometheus, Zipkin, SEQ) configured by user
  - VapeCache.Extensions.OpenTelemetry provides convenience helpers (optional)

### 3. **Separation of Platform Concerns**
- **No cloud platform dependencies in core library**
  - VapeCache.Infrastructure works anywhere: on-prem, cloud, edge
  - Platform integrations (Aspire, Kubernetes, AWS) are separate packages
  - Users install only what they need

### 4. **Explicit Over Implicit**
- **No hidden dependencies or magic configuration**
  - SEQ sink configuration lives in VapeCache.Console (example host)
  - VapeCache.Extensions.Serilog is opt-in (not required)
  - Users see exactly what they're installing

## NuGet Package Roadmap

### Immediate (Current Release)
- ✅ **VapeCache.Abstractions** - Core contracts
- ✅ **VapeCache.Infrastructure** - Redis transport + caching
  - ✅ Fixed: Removed Serilog sink dependencies (now uses ILogger<T> only)

### Phase 1: .NET Aspire Integration (Next 2 weeks)
- 🚧 **VapeCache.Extensions.Aspire**
  - Service discovery integration
  - Health checks
  - Aspire dashboard telemetry
  - Example Aspire AppHost project

### Phase 2: Optional Observability Extensions (Following 2 weeks)
- 🚧 **VapeCache.Extensions.Serilog** (optional)
  - VapeCache-specific enrichers
  - SEQ sink helpers
- 🚧 **VapeCache.Extensions.OpenTelemetry** (optional)
  - Pre-configured meter/trace providers
  - Common exporter helpers

### Phase 3: Advanced Integrations (Future)
- 📋 **VapeCache.Extensions.HealthChecks** (optional)
  - ASP.NET Core health check integration
  - Redis connectivity checks
  - Cache hit rate checks
- 📋 **VapeCache.Extensions.Kubernetes** (optional)
  - ConfigMap/Secret integration
  - Prometheus metrics endpoint
  - Liveness/readiness probes

## Migration Guide: Removing Serilog from VapeCache.Infrastructure

### What Changed
Previously, VapeCache.Infrastructure incorrectly depended on:
- ❌ `Serilog` (core)
- ❌ `Serilog.Sinks.Console`
- ❌ `Serilog.Sinks.File`

This violated separation of concerns - **libraries should not dictate logging implementation**.

### New Architecture
VapeCache.Infrastructure now depends ONLY on:
- ✅ `Microsoft.Extensions.Logging.Abstractions` (for ILogger<T>)
- ✅ `System.Diagnostics.DiagnosticSource` (for Activity/Meter - BCL types)

All logging is done via `ILogger<T>` interface. Users choose their implementation:
- **Serilog**: Install `Serilog.Extensions.Hosting` in your host project
- **NLog**: Install `NLog.Extensions.Logging` in your host project
- **Console Logger**: Built into `Microsoft.Extensions.Logging`

### User Impact
**If you're using VapeCache.Console**: No impact - Serilog configuration remains in VapeCache.Console where it belongs.

**If you're consuming VapeCache as a library**: You now have full control over logging implementation without VapeCache forcing dependencies on you.

## Example: Using VapeCache with Different Logging Providers

### Example 1: Serilog + SEQ (VapeCache.Console approach)

```csharp
// Host project .csproj
<ItemGroup>
  <PackageReference Include="VapeCache.Infrastructure" Version="1.0.0" />
  <PackageReference Include="Serilog.Sinks.Seq" Version="9.0.0" />
  <PackageReference Include="Serilog.Extensions.Hosting" Version="10.0.0" />
</ItemGroup>

// Program.cs
var builder = Host.CreateDefaultBuilder(args)
    .UseSerilog((context, services, config) =>
    {
        config
            .ReadFrom.Configuration(context.Configuration)
            .WriteTo.Seq("http://localhost:5341");
    })
    .ConfigureServices(services =>
    {
        services.AddVapecacheRedisConnections();
        services.AddVapecacheCaching();
    });
```

### Example 2: .NET Aspire (Future)

```csharp
// AppHost project
var builder = DistributedApplication.CreateBuilder(args);
var redis = builder.AddRedis("redis");
var api = builder.AddProject<Projects.MyApi>("api")
    .WithReference(redis);

// MyApi project
var builder = WebApplication.CreateBuilder(args);
builder.AddVapeCache()  // From VapeCache.Extensions.Aspire
    .WithRedisFromAspire("redis")
    .WithHealthChecks()
    .WithAspireTelemetry();
```

### Example 3: NLog

```csharp
// Host project .csproj
<ItemGroup>
  <PackageReference Include="VapeCache.Infrastructure" Version="1.0.0" />
  <PackageReference Include="NLog.Extensions.Logging" Version="5.3.0" />
</ItemGroup>

// Program.cs
var builder = Host.CreateDefaultBuilder(args)
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddNLog();
    })
    .ConfigureServices(services =>
    {
        services.AddVapecacheRedisConnections();
        services.AddVapecacheCaching();
    });
```

## FAQ

### Q: Why remove Serilog from VapeCache.Infrastructure?
**A**: Libraries should use `ILogger<T>` abstraction, not force a specific logging implementation on consumers. This allows users to choose Serilog, NLog, or any other provider.

### Q: Can I still use SEQ with VapeCache?
**A**: Yes! SEQ configuration belongs in your host project (see VapeCache.Console for example). VapeCache.Extensions.Serilog will provide optional helpers.

### Q: Will OpenTelemetry still work?
**A**: Yes! OpenTelemetry uses `ActivitySource` and `Meter` which are .NET BCL types with zero dependencies. Exporters are configured by users.

### Q: Why create separate extension packages?
**A**: Keeps core library lean and gives users choice. If you don't use Aspire, you don't install VapeCache.Extensions.Aspire. Principle of least surprise.

### Q: When will .NET Aspire integration be available?
**A**: Planned for next release (2 weeks). See [GitHub Projects](https://github.com/yourorg/vapecache/projects) for roadmap.

## References

- [Microsoft Logging Best Practices](https://learn.microsoft.com/en-us/dotnet/core/extensions/logging-providers)
- [.NET Aspire Overview](https://learn.microsoft.com/en-us/dotnet/aspire/get-started/aspire-overview)
- [OpenTelemetry for .NET](https://opentelemetry.io/docs/instrumentation/net/)
- [Serilog Best Practices](https://github.com/serilog/serilog/wiki/Getting-Started)
