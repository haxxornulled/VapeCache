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

When you enable endpoint mapping (`WithAutoMappedEndpoints(...)` or `MapVapeCacheEndpoints(...)`), the wrapper surface is:

- `GET /vapecache/status`
- `GET /vapecache/stats`
- `GET /vapecache/stream` (SSE)
- `GET /vapecache/intent/{key}`
- `GET /vapecache/intent?take=50`
- optional admin: `POST /vapecache/breaker/force-open`
- optional admin: `POST /vapecache/breaker/clear`

Autoscaler diagnostics are included in `status`, `stats`, and stream samples when diagnostics are registered.
See:
- [VapeCache.Extensions.Aspire/README.md](../VapeCache.Extensions.Aspire/README.md)
- [ENTERPRISE_MULTIPLEXER_AUTOSCALER.md](ENTERPRISE_MULTIPLEXER_AUTOSCALER.md)

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
│ builder.AddVapeCache()  // From VapeCache.Extensions.Aspire│
│     .WithRedisFromAspire("redis")  // Binds to resource     │
│     .WithHealthChecks()             // Adds health checks   │
│     .WithAspireTelemetry()          // OTel → Dashboard     │
│     .WithAspNetCoreOutputCaching()  // MVC/Blazor pipeline  │
│     .WithFailoverAffinityHints()    // Cluster failover hint │
│     .WithCacheStampedeProfile(      // Stampede defaults    │
│         CacheStampedeProfile.Balanced)                      │
│     .WithAutoMappedEndpoints();     // + status/stats/stream│
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

## Implementation Plan

### Phase 1: VapeCache.Extensions.Aspire Project

**File Structure:**
```
VapeCache.Extensions.Aspire/
├── VapeCache.Extensions.Aspire.csproj
├── AspireVapeCacheExtensions.cs      // builder.AddVapeCache()
├── AspireRedisResourceExtensions.cs  // .WithRedisFromAspire()
├── AspireHealthCheckExtensions.cs    // .WithHealthChecks()
├── AspireTelemetryExtensions.cs      // .WithAspireTelemetry()
├── VapeCacheHealthCheck.cs           // IHealthCheck implementation
└── RedisHealthCheck.cs               // IHealthCheck implementation
```

**Dependencies:**
```xml
<PackageReference Include="Aspire.Hosting.Redis" Version="9.0.0" />
<PackageReference Include="Microsoft.Extensions.ServiceDiscovery" Version="9.0.0" />
<PackageReference Include="Microsoft.Extensions.Diagnostics.HealthChecks" Version="10.0.0" />
<PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.10.0" />
```

### Phase 2: API Design

#### Extension Method: `AddVapeCache()`

```csharp
namespace Microsoft.Extensions.Hosting;

public static class AspireVapeCacheExtensions
{
    /// <summary>
    /// Adds VapeCache services configured for .NET Aspire.
    /// </summary>
    public static AspireVapeCacheBuilder AddVapeCache(
        this IHostApplicationBuilder builder)
    {
        // Register core VapeCache services
        builder.Services.AddVapecacheRedisConnections();
        builder.Services.AddVapecacheCaching();

        // Return builder for fluent configuration
        return new AspireVapeCacheBuilder(builder);
    }
}

public sealed class AspireVapeCacheBuilder
{
    public IHostApplicationBuilder Builder { get; }

    internal AspireVapeCacheBuilder(IHostApplicationBuilder builder)
    {
        Builder = builder;
    }
}
```

#### Extension Method: `WithRedisFromAspire()`

```csharp
public static class AspireRedisResourceExtensions
{
    /// <summary>
    /// Configures VapeCache to use Redis connection string from Aspire resource.
    /// Leverages Aspire's built-in service discovery to bind connection configuration.
    /// </summary>
    /// <param name="builder">VapeCache builder.</param>
    /// <param name="connectionName">Name of the Redis resource in AppHost.</param>
    public static AspireVapeCacheBuilder WithRedisFromAspire(
        this AspireVapeCacheBuilder builder,
        string connectionName)
    {
        // IMPORTANT: We do NOT read IConfiguration directly here.
        // Instead, we configure IOptions<RedisConnectionOptions> to bind from
        // Aspire's service discovery configuration source.
        //
        // Aspire automatically injects connection strings via IConfiguration when
        // .WithReference(redis) is called in AppHost. The connection string appears as:
        // ConnectionStrings:{connectionName}
        //
        // The host (Program.cs) will bind this to RedisConnectionOptions.

        builder.Builder.Services.Configure<RedisConnectionOptions>(options =>
        {
            // This callback is invoked AFTER IConfiguration is built by the host.
            // We access the connection string via the host's configuration.
            // This is acceptable because we're in an extension method that's part of
            // the host's composition root, not the library itself.

            var connectionString = builder.Builder.Configuration
                .GetConnectionString(connectionName);

            if (!string.IsNullOrEmpty(connectionString))
            {
                options.ConnectionString = connectionString;
            }
        });

        // Alternative approach: Let the host bind configuration explicitly
        // (see CONFIGURATION_BEST_PRACTICES.md for why this is preferred)
        //
        // Instead of reading IConfiguration here, we could:
        // 1. Document that users should bind ConnectionStrings:{connectionName} to RedisConnectionOptions
        // 2. Provide a helper that returns the configuration path to bind
        //
        // Example:
        // builder.Builder.Services
        //     .AddOptions<RedisConnectionOptions>()
        //     .Bind(builder.Builder.Configuration.GetSection($"ConnectionStrings:{connectionName}"));

        return builder;
    }
}
```

#### Extension Method: `WithHealthChecks()`

```csharp
public static class AspireHealthCheckExtensions
{
    /// <summary>
    /// Adds VapeCache and Redis health checks to the application.
    /// </summary>
    public static AspireVapeCacheBuilder WithHealthChecks(
        this AspireVapeCacheBuilder builder)
    {
        builder.Builder.Services
            .AddHealthChecks()
            .AddCheck<RedisHealthCheck>(
                "redis",
                tags: new[] { "ready", "live" })
            .AddCheck<VapeCacheHealthCheck>(
                "vapecache",
                tags: new[] { "ready" });

        return builder;
    }
}

// Health check implementation
internal sealed class RedisHealthCheck : IHealthCheck
{
    private readonly IRedisCommandExecutor _redis;

    public RedisHealthCheck(IRedisCommandExecutor redis)
    {
        _redis = redis;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _redis.PingAsync(cancellationToken);
            return HealthCheckResult.Healthy("Redis is responsive");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(
                "Redis is not responsive",
                ex);
        }
    }
}

internal sealed class VapeCacheHealthCheck : IHealthCheck
{
    private readonly IRedisCircuitBreakerState _breaker;
    private readonly ICacheStats _stats;

    public VapeCacheHealthCheck(
        IRedisCircuitBreakerState breaker,
        ICacheStats stats)
    {
        _breaker = breaker;
        _stats = stats;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var snapshot = _stats.Snapshot;
        var totalReads = snapshot.Hits + snapshot.Misses;
        var hitRate = totalReads <= 0 ? 0d : (double)snapshot.Hits / totalReads;

        var data = new Dictionary<string, object>
        {
            ["breaker_open"] = _breaker.IsOpen,
            ["consecutive_failures"] = _breaker.ConsecutiveFailures,
            ["hit_count"] = snapshot.Hits,
            ["miss_count"] = snapshot.Misses,
            ["hit_rate"] = hitRate
        };

        if (_breaker.IsForcedOpen)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                "Redis circuit breaker is forced open (manual failover)",
                data: data));
        }

        if (_breaker.IsOpen)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                "Redis circuit breaker is open (automatic failover)",
                data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            "VapeCache is operating normally",
            data: data));
    }
}
```

#### Extension Method: `WithAspireTelemetry()`

```csharp
public static class AspireTelemetryExtensions
{
    /// <summary>
    /// Configures OpenTelemetry to send VapeCache metrics/traces to Aspire Dashboard.
    /// </summary>
    public static AspireVapeCacheBuilder WithAspireTelemetry(
        this AspireVapeCacheBuilder builder)
    {
        // Aspire automatically configures OTLP endpoint via environment variables
        // We just need to register VapeCache meters/activity sources

        builder.Builder.Services
            .AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddMeter("VapeCache.Redis");
                metrics.AddMeter("VapeCache.Cache");
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource("VapeCache.Redis");
            });

        return builder;
    }
}
```

### Phase 3: Example Aspire AppHost

**MyApp.AppHost/Program.cs:**
```csharp
var builder = DistributedApplication.CreateBuilder(args);

// Add Redis resource with persistent volume
var redis = builder.AddRedis("redis")
    .WithDataVolume()
    .WithRedisCommander();  // Optional: Redis UI at http://localhost:8081

// Add your API with VapeCache
var api = builder.AddProject<Projects.MyApi>("api")
    .WithReference(redis)  // Injects ConnectionStrings:redis
    .WithExternalHttpEndpoints();

// Add Aspire Dashboard (automatic)
// http://localhost:15888

builder.Build().Run();
```

**MyApi/Program.cs:**
```csharp
var builder = WebApplication.CreateBuilder(args);

// Single-line VapeCache + Aspire setup
builder.AddVapeCache()
    .WithRedisFromAspire("redis")  // Binds to AppHost Redis resource
    .WithHealthChecks()             // Registers Redis + VapeCache health checks
    .WithAspireTelemetry()          // Sends to Aspire Dashboard
    .WithCacheStampedeProfile(CacheStampedeProfile.Balanced)
    .WithAutoMappedEndpoints();     // /vapecache/status + /vapecache/stats + /vapecache/stream

var app = builder.Build();

// Health check mapping (for Kubernetes, Azure Container Apps, etc.)
app.MapHealthChecks("/health");
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live")
});

app.Run();
```

### Phase 4: Testing Strategy

**Unit Tests:**
```csharp
[Fact]
public void AddVapeCache_RegistersRequiredServices()
{
    var builder = WebApplication.CreateBuilder();
    builder.AddVapeCache();

    var app = builder.Build();

    Assert.NotNull(app.Services.GetService<ICacheService>());
    Assert.NotNull(app.Services.GetService<IRedisCommandExecutor>());
}

[Fact]
public async Task RedisHealthCheck_ReturnsHealthy_WhenRedisIsAvailable()
{
    // Arrange
    var redis = Substitute.For<IRedisCommandExecutor>();
    redis.PingAsync(default).Returns(ValueTask.CompletedTask);

    var healthCheck = new RedisHealthCheck(redis);

    // Act
    var result = await healthCheck.CheckHealthAsync(null!);

    // Assert
    Assert.Equal(HealthStatus.Healthy, result.Status);
}
```

**Integration Tests (with Aspire TestHost):**
```csharp
public class AspireIntegrationTests
{
    [Fact]
    public async Task VapeCache_WorksWithAspireRedisResource()
    {
        // Arrange
        await using var app = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.MyApp_AppHost>();

        await app.StartAsync();

        // Act
        var httpClient = app.CreateHttpClient("api");
        var response = await httpClient.GetAsync("/health");

        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("Healthy", content);
    }
}
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

### Logs Tab (Structured Logs)
- All VapeCache logs (via ILogger<T>)
- Filtered by service (api, redis)
- Correlated with traces via TraceId

## Deployment

### Azure Container Apps
```bash
# Aspire CLI (coming soon)
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

## Roadmap

- ✅ **Phase 1**: Remove Serilog from VapeCache.Infrastructure (DONE)
- ✅ **Phase 2**: Create VapeCache.Extensions.Aspire project (DONE)
  - ✅ Fluent API extensions (AddVapeCache, WithRedisFromAspire, WithHealthChecks, WithAspireTelemetry)
  - ✅ Health check implementations (RedisHealthCheck, VapeCacheHealthCheck)
  - ✅ OpenTelemetry integration (cache hit/miss metrics, Redis traces)
  - ✅ Package documentation (README.md)
- 📋 **Phase 3**: Add Aspire AppHost example
- 📋 **Phase 4**: Integration tests with Aspire TestHost
- 📋 **Phase 5**: Publish to NuGet

## References

- [.NET Aspire Documentation](https://learn.microsoft.com/en-us/dotnet/aspire/)
- [Aspire Redis Component](https://learn.microsoft.com/en-us/dotnet/aspire/caching/stackexchange-redis-component)
- [Aspire Health Checks](https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/health-checks)
- [Aspire Service Discovery](https://learn.microsoft.com/en-us/dotnet/aspire/service-discovery/overview)
