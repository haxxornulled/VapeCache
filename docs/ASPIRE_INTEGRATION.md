# .NET Aspire Integration for VapeCache

## Overview

`VapeCache.Extensions.Aspire` gives you a clean Aspire wiring layer:
- register cache services
- bind Redis from service discovery
- expose health/diagnostics endpoints
- emit OpenTelemetry metrics/traces
- wire ASP.NET Core output-caching middleware to VapeCache store

No service locator patterns and no route clutter in `Program.cs`.

## Minimal API Endpoints (Wrapper Surface)

When you enable endpoint mapping (`WithAutoMappedEndpoints(...)` with `options.Enabled = true` or `MapVapeCacheEndpoints(...)`), the diagnostics wrapper surface is:

- `GET /vapecache/status`
- `GET /vapecache/stats`
- `GET /vapecache/stream` (SSE)
- `GET /vapecache/dashboard` (built-in realtime UI)
- `GET /vapecache/intent/{key}`
- `GET /vapecache/intent?take=50`
- admin control surface (separate prefix): `POST /vapecache/admin/breaker/force-open`
- admin control surface (separate prefix): `POST /vapecache/admin/breaker/clear`

Use `MapVapeCacheAdminEndpoints(...)` (or `WithAutoMappedEndpoints` with `IncludeBreakerControlEndpoints = true`) for breaker controls and keep that prefix internal-only.

The built-in dashboard is implemented as a Vite + TypeScript frontend under `VapeCache.Extensions.Aspire/dashboard-ui` and served by Aspire endpoints from embedded assets.

Autoscaler diagnostics and per-lane mux diagnostics are included in `status`, `stats`, and stream samples when diagnostics are registered.
See:
- [VapeCache.Extensions.Aspire/README.md](../VapeCache.Extensions.Aspire/README.md)
- [ENTERPRISE_MULTIPLEXER_AUTOSCALER.md](ENTERPRISE_MULTIPLEXER_AUTOSCALER.md)
- [ASPIRE_LANE_QUERY_PACK.md](ASPIRE_LANE_QUERY_PACK.md)

## What is .NET Aspire?

.NET Aspire is a cloud-ready stack for building distributed applications that includes:
- **Service Discovery**: Automatic connection string resolution from resources
- **Health Checks**: Built-in liveness/readiness probes
- **Telemetry**: OpenTelemetry integration with Aspire Dashboard
- **Local Development**: Docker-based local dev experience
- **Deployment**: One-click deploy to Azure Container Apps, Kubernetes, etc.

## VapeCache + Aspire Integration Goals

### Developer Experience
✅ **Low-friction setup**: `builder.AddVapeCache().WithRedisFromAspire("redis")`
✅ **One-call composition**: `builder.AddVapeCacheKitchenSink(...)`
✅ **Production observability baseline**: `builder.AddVapeCache().WithProductionObservability()`
✅ **Minimal config**: Aspire resources auto-configure connection strings
✅ **Local dev parity**: Same code runs in dev (Docker) and prod (Azure)
✅ **Observable by default**: Metrics/traces flow to Aspire Dashboard

### Production Features
✅ **Health checks**: Registered checks you map in your host as needed
✅ **Service discovery**: Dynamic Redis endpoint resolution
✅ **Resilience**: Circuit breaker pre-configured
✅ **Telemetry**: Aspire Dashboard shows VapeCache metrics/traces

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│ Aspire AppHost (Orchestrator)                               │
├─────────────────────────────────────────────────────────────┤
│ var builder = DistributedApplication.CreateBuilder(args);  │
│                                                              │
│ var redis = builder.AddRedis("redis")                      │
│     .WithDataVolume()                                       │
│     .WithRedisCommander();  // Optional UI                  │
│                                                              │
│ var cache = builder.AddProject<Projects.MyApi>("api")      │
│     .WithReference(redis);  // Injects connection string    │
└─────────────────────────────────────────────────────────────┘
                              │
                              │ Service Discovery
                              ▼
┌─────────────────────────────────────────────────────────────┐
│ MyApi Project (Your Application)                            │
├─────────────────────────────────────────────────────────────┤
│ var builder = WebApplication.CreateBuilder(args);          │
│                                                              │
│ var vapeCache = builder.AddVapeCache()                     │
│     .WithRedisFromAspire("redis")                          │
│     .WithProductionObservability();                        │
│                                                            │
│ // Optional app-hosted diagnostic surface (protected):     │
│ vapeCache.WithAutoMappedEndpoints(options =>               │
│ {                                                          │
│     options.Enabled = true;                                │
│     options.EnableDashboard = true;                        │
│ });                                                        │
│                                                              │
│ var app = builder.Build();                                  │
│ app.MapHealthChecks("/health");                             │
│ app.Run();                                                   │
└─────────────────────────────────────────────────────────────┘
                              │
                              │ Connection String Injected
                              ▼
┌─────────────────────────────────────────────────────────────┐
│ VapeCache.Infrastructure                                    │
├─────────────────────────────────────────────────────────────┤
│ RedisConnectionOptions:                                     │
│   ConnectionString: "redis://localhost:6379"  ← From Aspire │
└─────────────────────────────────────────────────────────────┘
```

## Aspire Dashboard Integration

When you run your Aspire app, the dashboard (`http://localhost:15888`) will show:

### Resources Tab
- **redis**: Redis container status, port mappings, logs
- **api**: Your API status, logs

### Metrics Tab (VapeCache Metrics)
- `redis.cmd.calls`: Total Redis commands
- `redis.cmd.failures`: Failed commands
- `redis.cmd.ms`: Command latency histogram
- `redis.pool.acquires`: Pool lease requests
- `redis.pool.timeouts`: Pool timeouts
- `cache.hits`: Cache hit count
- `cache.misses`: Cache miss count
- `cache.fallback_to_memory`: Redis fallback events
- `cache.set.payload.bytes`: Payload size histogram for cache writes
- `cache.set.large_key`: Large payload writes (>64 KB)
- `cache.evictions`: In-memory evictions (by reason)
- `cache.stampede.key_rejected`: Invalid/suspicious stampede key rejections
- `cache.stampede.lock_wait_timeout`: Stampede lock wait timeouts
- `cache.stampede.failure_backoff_rejected`: Stampede backoff rejections
- `efcore.cache.query.execution.completed`: EF Core query executions (when `VapeCache.Extensions.EntityFrameworkCore.OpenTelemetry` is installed)
- `efcore.cache.query.execution.failed`: EF Core query failures (when EF Core OTEL package is installed)
- `efcore.cache.query.execution.ms`: EF Core query execution duration histogram
- `efcore.cache.invalidation.zone.invalidated`: EF Core-derived zone invalidations
- `efcore.cache.invalidation.zone.failed`: EF Core-derived zone invalidation failures

### Wrapper Endpoint Payload

`GET /vapecache/status` and `GET /vapecache/stats` include:
- `stampedeKeyRejected`
- `stampedeLockWaitTimeout`
- `stampedeFailureBackoffRejected`
- `spill.mode` (`noop` or `file`)
- `spill.totalSpillFiles`, `spill.activeShards`, `spill.maxFilesInShard`
- `spill.imbalanceRatio` and `spill.topShards` for scatter health

`GET /vapecache/stream` provides realtime SSE frames (`event: vapecache-stats`) for Blazor charting.

### Traces Tab (Distributed Tracing)
- HTTP request → Cache lookup → Redis command
- Trace IDs link logs, metrics, and traces
- Flamegraph visualization of request latency
- EF Core second-level cache activities from source `VapeCache.EFCore.Cache` when EF Core OTEL package is installed

### Logs Tab (Structured Logs)
- All VapeCache logs (via ILogger<T>)
- Filtered by service (api, redis)
- Correlated with traces via TraceId

## Deployment

### Azure Container Apps
```bash
# Azure Developer CLI
azd init
azd up  # Deploys to Azure with VapeCache configured
```

### Kubernetes
```bash
# Generate manifests
dotnet publish /t:GenerateAspireKubernetesManifests

# Apply to cluster
kubectl apply -f ./manifests
```

## Benefits Summary

| Feature | Without Aspire | With Aspire |
|---------|---------------|-------------|
| **Connection Strings** | Manual appsettings.json | Auto-injected from resources |
| **Local Redis** | Manual Docker run | One-line `.AddRedis()` |
| **Health Checks** | Manual implementation | `.WithHealthChecks()` |
| **Telemetry** | Manual OTel config | Auto-configured dashboard |
| **Service Discovery** | Hardcoded hosts | Dynamic resolution |
| **Deployment** | Manual YAML/Bicep | `azd up` |
| **Dev/Prod Parity** | Different configs | Same code everywhere |

## References

- [.NET Aspire Documentation](https://learn.microsoft.com/en-us/dotnet/aspire/)
- [Aspire Redis Component](https://learn.microsoft.com/en-us/dotnet/aspire/caching/stackexchange-redis-component)
- [Aspire Health Checks](https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/health-checks)
- [Aspire Service Discovery](https://learn.microsoft.com/en-us/dotnet/aspire/service-discovery/overview)
