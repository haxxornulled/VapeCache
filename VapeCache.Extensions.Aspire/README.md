# VapeCache.Extensions.Aspire

.NET Aspire integration for VapeCache - Service discovery, health checks, and telemetry.

## Features

✅ **Service Discovery** - Auto-configure Redis connection from Aspire resources
✅ **Health Checks** - Redis connectivity and circuit breaker monitoring
✅ **Telemetry** - Cache hit/miss metrics visible in Aspire Dashboard
✅ **Distributed Tracing** - End-to-end traces for Redis operations
✅ **Zero Configuration** - Single line to enable all features

## Installation

```bash
dotnet add package VapeCache.Extensions.Aspire
```

## Quick Start

### 1. AppHost (Aspire Orchestrator)

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// Add Redis resource
var redis = builder.AddRedis("redis");

// Add your API with Redis reference
var api = builder.AddProject<Projects.MyApi>("api")
    .WithReference(redis);  // Injects connection string

builder.Build().Run();
```

### 2. API Project (Your Application)

```csharp
var builder = WebApplication.CreateBuilder(args);

// Single line to add VapeCache with full Aspire integration
builder.AddVapeCache()
    .WithRedisFromAspire("redis")     // Bind to AppHost Redis resource
    .WithHealthChecks()                // Add health checks (host maps endpoints)
    .WithAspireTelemetry();            // Send metrics to Aspire Dashboard

var app = builder.Build();

app.MapHealthChecks("/health");
app.Run();
```

### 3. Use the Cache

```csharp
public class UserService
{
    private readonly ICacheService _cache;

    public UserService(ICacheService cache) => _cache = cache;

    public async Task<User?> GetUserAsync(int id, CancellationToken ct)
    {
        var key = $"user:{id}";
        return await _cache.GetOrSetAsync(
            key,
            async ct => await _db.Users.FindAsync(id, ct),
            (writer, user) => JsonSerializer.Serialize(writer, user),
            bytes => JsonSerializer.Deserialize<User>(bytes),
            new CacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) },
            ct);
    }
}
```

## Aspire Dashboard

Navigate to `http://localhost:15888` to view:

### Metrics

- `cache.current.backend` - **Current active backend** (1=redis, 0=in-memory) - Real-time visibility
- `cache.get.hits` - Cache hits (by backend: redis, in-memory, hybrid)
- `cache.get.misses` - Cache misses
- `cache.fallback.to_memory` - Circuit breaker fallback events
- `cache.op.ms` - Operation latency
- `redis.cmd.calls` - Redis commands executed
- `redis.pool.wait.ms` - Connection pool wait time

### Traces

End-to-end distributed traces showing:
- Cache operation → Pool acquisition → Redis command → Response parsing

### Health Checks

- **redis**: Redis connectivity (PING validation)
- **vapecache**: Circuit breaker state and cache statistics

## API Reference

### `AddVapeCache()`

Registers core VapeCache services.

```csharp
builder.AddVapeCache()
```

### `WithRedisFromAspire(connectionName)`

Configures Redis connection from Aspire service discovery.

```csharp
.WithRedisFromAspire("redis")  // Matches AppHost resource name
```

### `WithHealthChecks()`

Adds health checks for Redis and VapeCache.

```csharp
.WithHealthChecks()

// Map in your host:
app.MapHealthChecks("/health");
app.MapHealthChecks("/health/redis", new HealthCheckOptions
{
    Predicate = check => check.Name == "redis"
});
```

### `WithAspireTelemetry()`

Configures OpenTelemetry to send metrics/traces to Aspire Dashboard.

```csharp
.WithAspireTelemetry()
```

## Health Check Details

### Redis Health Check (`redis`)

- **Healthy**: Redis is reachable and can execute commands
- **Degraded**: Connection pool timeout (under pressure)
- **Unhealthy**: Redis connection failed

### VapeCache Health Check (`vapecache`)

- **Healthy**: Circuit breaker closed, no failures
- **Degraded**: Circuit breaker open (using in-memory fallback) OR consecutive failures
- **Unhealthy**: N/A (degraded is worst case)

**Diagnostic Data:**
```json
{
  "circuit_breaker_open": false,
  "consecutive_failures": 0,
  "hit_count": 12345,
  "miss_count": 678,
  "hit_rate": 0.948
}
```

## Production Deployment

### Azure Container Apps

```bash
azd up  # Deploy via Aspire
```

Health checks are automatically configured for liveness/readiness probes.

### Kubernetes

```yaml
livenessProbe:
  httpGet:
    path: /health/redis
    port: 8080
  initialDelaySeconds: 10
  periodSeconds: 30

readinessProbe:
  httpGet:
    path: /health
    port: 8080
  initialDelaySeconds: 5
  periodSeconds: 10
```

## License

Apache 2.0

## See Also

- [VapeCache Documentation](https://github.com/haxxornulled/VapeCache/tree/main/docs)
- [.NET Aspire Documentation](https://learn.microsoft.com/en-us/dotnet/aspire/)
- [Aspire Dashboard Integration Guide](https://github.com/haxxornulled/VapeCache/blob/main/docs/ASPIRE_CACHE_METRICS.md)
