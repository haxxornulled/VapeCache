# VapeCache.Extensions.Aspire

Wire VapeCache into .NET Aspire without Program.cs boilerplate.
You get service discovery, health checks, telemetry, and wrapper endpoints in one fluent chain.

## Features

✅ **Service Discovery** - Auto-configure Redis connection from Aspire resources
✅ **Health Checks** - Redis connectivity and circuit breaker monitoring
✅ **Telemetry** - Cache hit/miss metrics visible in Aspire Dashboard
✅ **Distributed Tracing** - End-to-end traces for Redis operations
✅ **Wrapper Endpoints** - `MapVapeCacheEndpoints(...)` for status/stats/admin surfaces
✅ **SEQ by Default** - OTLP exporter falls back to Seq when no endpoint is configured
✅ **Fluent Telemetry API** - `.UseSeq(...)`, custom headers, and wrapper callbacks
✅ **Fluent Stampede Profiles** - `.WithCacheStampedeProfile(...)` with optional overrides
✅ **ASP.NET Core Pipeline Hook** - `.WithAspNetCoreOutputCaching(...)` for MVC/Blazor/minimal output cache store
✅ **Failover Affinity Hints** - `.WithFailoverAffinityHints(...)` for cluster/web-garden sticky-session routing
✅ **Low Ceremony** - Single fluent chain to enable all major features

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

// One fluent chain for full Aspire integration
builder.AddVapeCache()
    .WithRedisFromAspire("redis")     // Bind to AppHost Redis resource
    .WithHealthChecks()                // Add health checks (host maps endpoints)
    .WithAspireTelemetry()             // Send metrics to Aspire Dashboard
    .WithAspNetCoreOutputCaching()     // Replace output-cache store with VapeCache
    .WithFailoverAffinityHints()       // Emit sticky-session hints during failover
    .WithCacheStampedeProfile(CacheStampedeProfile.Balanced)
    .WithAutoMappedEndpoints();        // Auto-maps /vapecache/status + /vapecache/stats + /vapecache/stream

var app = builder.Build();

app.UseVapeCacheOutputCaching();
app.UseVapeCacheFailoverAffinityHints();
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
            new CacheEntryOptions(
                Ttl: TimeSpan.FromMinutes(5),
                Intent: new CacheIntent(CacheIntentKind.ReadThrough, Reason: "user detail lookup")),
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
- `cache.set.payload.bytes` - Payload size histogram for writes (large-key visibility)
- `cache.set.large_key` - Large payload writes (>64 KB)
- `cache.evictions` - In-memory eviction count (tagged by eviction reason)
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

Uses the AppHost resource name so connection details come from Aspire service discovery.

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

Configures OpenTelemetry for VapeCache metrics/traces and OTLP export.
Resolution order for OTLP endpoint:

1. `options.OtlpEndpoint`
2. `OpenTelemetry:Otlp:Endpoint` (configuration)
3. `OTEL_EXPORTER_OTLP_ENDPOINT` (environment)
4. `DOTNET_DASHBOARD_OTLP_ENDPOINT_URL` (Aspire dashboard fallback)
5. Seq default: `http://localhost:5341/ingest/otlp`

```csharp
.WithAspireTelemetry()
```

### `WithCacheStampedeProfile(profile, configure?)`

Applies named stampede defaults with optional fluent overrides.

```csharp
.WithCacheStampedeProfile(
    CacheStampedeProfile.Balanced,
    options => options.WithLockWaitTimeout(TimeSpan.FromMilliseconds(600)));
```

### `WithAspNetCoreOutputCaching(configureOutputCache?, configureStore?)`

Adds ASP.NET Core output caching and swaps the default store for `VapeCacheOutputCacheStore`.

```csharp
builder.AddVapeCache()
    .WithAspNetCoreOutputCaching(
        configureOutputCache: options =>
        {
            options.AddBasePolicy(policy => policy.Expire(TimeSpan.FromSeconds(30)));
        },
        configureStore: store =>
        {
            store.KeyPrefix = "vapecache:output";
            store.DefaultTtl = TimeSpan.FromSeconds(30);
            store.EnableTagIndexing = true;
        });

var app = builder.Build();
app.UseVapeCacheOutputCaching();
```

### `WithFailoverAffinityHints(configure?)`

Adds options for middleware that emits node-affinity hints during failover:

```csharp
builder.AddVapeCache()
    .WithFailoverAffinityHints(options =>
    {
        options.NodeId = Environment.MachineName;
        options.CookieName = "VapeCacheAffinity";
    });

var app = builder.Build();
app.UseVapeCacheFailoverAffinityHints();
```

### `MapVapeCacheEndpoints(prefix, includeBreakerControlEndpoints, includeLiveStreamEndpoint, includeIntentEndpoints)`

Maps wrapper-facing HTTP endpoints:

- `GET {prefix}/status`
- `GET {prefix}/stats`
- `GET {prefix}/stream` (Server-Sent Events realtime channel)
- `POST {prefix}/breaker/force-open` (optional)
- `POST {prefix}/breaker/clear` (optional)

Minimal API contract notes:
- `status` returns backend state, cache stats, breaker state, and autoscaler diagnostics (when available).
- `stats` returns cache counters + hit-rate + autoscaler diagnostics (when available).
- `stream` emits SSE `event: vapecache-stats` frames with the live sample payload.
- breaker endpoints are intentionally opt-in and should be protected with authN/authZ.

```csharp
var app = builder.Build();

app.MapVapeCacheEndpoints("/vapecache");

// Optional admin controls (protect with authN/authZ):
app.MapVapeCacheEndpoints(
    prefix: "/vapecache-admin",
    includeBreakerControlEndpoints: true,
    includeLiveStreamEndpoint: true,
    includeIntentEndpoints: true);
```

`GET {prefix}/status` and `GET {prefix}/stats` include the stampede hardening counters:
- `stampedeKeyRejected`
- `stampedeLockWaitTimeout`
- `stampedeFailureBackoffRejected`

They also include autoscaler diagnostics when `IRedisMultiplexerDiagnostics` is available:
- `autoscaler.currentConnections`
- `autoscaler.targetConnections`
- `autoscaler.highSignalCount`
- `autoscaler.timeoutRatePerSec`
- `autoscaler.rollingP95LatencyMs`
- `autoscaler.rollingP99LatencyMs`
- `autoscaler.unhealthyConnections`
- `autoscaler.reconnectFailureRatePerSec`
- `autoscaler.scaleEventsInCurrentMinute`
- `autoscaler.maxScaleEventsPerMinute`
- `autoscaler.frozen`
- `autoscaler.freezeReason`
- `autoscaler.lastScaleDirection`
- `autoscaler.lastScaleReason`

`/stream` emits `event: vapecache-stats` frames with a JSON payload compatible with Blazor realtime chart components.

Example payload:
```json
{
  "timestampUtc": "2026-02-24T20:54:00.0000000+00:00",
  "currentBackend": "redis",
  "hits": 123456,
  "misses": 7890,
  "setCalls": 45678,
  "removeCalls": 321,
  "fallbackToMemory": 12,
  "redisBreakerOpened": 2,
  "stampedeKeyRejected": 0,
  "stampedeLockWaitTimeout": 1,
  "stampedeFailureBackoffRejected": 0,
  "hitRate": 0.9399,
  "autoscaler": {
    "enabled": true,
    "currentConnections": 6,
    "targetConnections": 7,
    "minConnections": 4,
    "maxConnections": 16,
    "currentReadLanes": 3,
    "currentWriteLanes": 3,
    "highSignalCount": 2,
    "avgInflightUtilization": 0.81,
    "avgQueueDepth": 7.4,
    "maxQueueDepth": 34,
    "timeoutRatePerSec": 0.0,
    "rollingP95LatencyMs": 22.7,
    "rollingP99LatencyMs": 34.0,
    "unhealthyConnections": 0,
    "reconnectFailureRatePerSec": 0.0,
    "scaleEventsInCurrentMinute": 1,
    "maxScaleEventsPerMinute": 2,
    "frozen": false,
    "frozenUntilUtc": null,
    "freezeReason": null,
    "lastScaleEventUtc": "2026-02-24T21:02:08.0000000+00:00",
    "lastScaleDirection": "up",
    "lastScaleReason": "inflight+queue"
  }
}
```

### `WithAutoMappedEndpoints(options => ...)`

Registers a startup filter that maps VapeCache endpoints automatically so you don't need to wire them in `Program.cs`.

```csharp
builder.AddVapeCache()
    .WithAutoMappedEndpoints(options =>
    {
        options.Prefix = "/cache";
        options.IncludeBreakerControlEndpoints = false;
        options.EnableLiveStream = true;
        options.LiveSampleInterval = TimeSpan.FromMilliseconds(500);
        options.LiveChannelCapacity = 512;
    });
```

Enterprise transport/autoscaler architecture and tuning:
- [`docs/ENTERPRISE_MULTIPLEXER_AUTOSCALER.md`](../docs/ENTERPRISE_MULTIPLEXER_AUTOSCALER.md)

### Custom Wrapper/Exporter Scenario

```csharp
builder.AddVapeCache()
    .WithAspireTelemetry(options =>
    {
        options.UseSeq(seqBaseUrl: "http://localhost:5341", apiKey: "dev-seq-key")
               .WithOtlpHeader("x-env", "dev")
               .AddMetricsConfiguration(m =>
               {
                   // Add custom metric exporter extensions here
               })
               .AddTracingConfiguration(t =>
               {
                   // Add custom trace exporter extensions here
               });
    });
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

## Blazor Realtime Example

See `docs/BLAZOR_DASHBOARD_EXAMPLE.md` for a full Blazor component and stream client using:

- `GET /vapecache/stream` (SSE realtime feed)
- `GET /vapecache/status` (snapshot fallback)

## See Also

- [VapeCache Documentation](https://github.com/haxxornulled/VapeCache/tree/main/docs)
- [.NET Aspire Documentation](https://learn.microsoft.com/en-us/dotnet/aspire/)
- [Aspire Dashboard Integration Guide](https://github.com/haxxornulled/VapeCache/blob/main/docs/ASPIRE_CACHE_METRICS.md)
