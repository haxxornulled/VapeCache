# VapeCache Configuration

VapeCache uses the `IOptions<T>` pattern. Configuration is owned by the host (appsettings/environment variables), and VapeCache consumes options via DI.

For logging/telemetry-specific keys and fallback behavior, use:

- [LOGGING_TELEMETRY_CONFIGURATION.md](LOGGING_TELEMETRY_CONFIGURATION.md)

## Minimal Configuration

```json
{
  "RedisConnection": {
    "Host": "localhost",
    "Port": 6379
  }
}
```

Or provide a connection string:

```bash
$env:VAPECACHE_REDIS_CONNECTIONSTRING = "redis://localhost:6379/0"
```

Bind the section before you register the core services:

```csharp
using VapeCache.Abstractions.Connections;

builder.Services.AddOptions<RedisConnectionOptions>()
    .Bind(builder.Configuration.GetSection("RedisConnection"));

builder.Services.AddVapecacheRedisConnections();
builder.Services.AddVapecacheCaching();
```

## RedisConnection (RedisConnectionOptions)

Controls connection pooling and socket behavior.

```json
{
  "RedisConnection": {
    "Host": "redis.example.com",
    "Port": 6380,
    "Username": "admin",
    "Password": "secret",
    "Database": 0,
    "ConnectionString": null,
    "UseTls": true,
    "TlsHost": "redis.example.com",
    "AllowInvalidCert": false,
    "MaxConnections": 64,
    "MaxIdle": 64,
    "Warm": 4,
    "ConnectTimeout": "00:00:05",
    "AcquireTimeout": "00:00:02",
    "ValidateAfterIdle": "00:00:30",
    "ValidateTimeout": "00:00:00.500",
    "IdleTimeout": "00:05:00",
    "MaxConnectionLifetime": "01:00:00",
    "ReaperPeriod": "00:00:10",
    "EnableTcpNoDelay": true,
    "TcpSendBufferBytes": 4194304,
    "TcpReceiveBufferBytes": 4194304,
    "EnableTcpKeepAlive": true,
    "TcpKeepAliveTime": "00:00:30",
    "TcpKeepAliveInterval": "00:00:10",
    "AllowAuthFallbackToPasswordOnly": false,
    "LogWhoAmIOnConnect": false,
    "MaxBulkStringBytes": 16777216,
    "MaxArrayDepth": 64,
    "RespProtocolVersion": 3,
    "EnableClusterRedirection": true,
    "MaxClusterRedirects": 3
  }
}
```

**Notes**
- `ConnectionString` overrides host/port/user/password/database when set.
- `AllowInvalidCert=true` is blocked in production environments.
- `MaxConnections` and `MaxIdle` drive the connection pool size.
- Defaults are full-tilt for high-throughput links (`TcpSendBufferBytes=4MB`, `TcpReceiveBufferBytes=4MB`).
- `EnableTcpNoDelay=true` is best for low-latency cache workloads.
- `TcpSendBufferBytes` / `TcpReceiveBufferBytes` let you tune socket buffering for high-throughput links.
- `RespProtocolVersion` supports `2` or `3`; default negotiation is `RESP2` unless configured.
- `EnableClusterRedirection=true` enables MOVED/ASK retries on cache-path commands.
- `MaxClusterRedirects` bounds redirect hops per command (default `3`, clamped `0..16`).
- `AllowAuthFallbackToPasswordOnly=false` is the safe default; only enable it when you intentionally want legacy password-only fallback after ACL auth fails.

## RedisMultiplexer (RedisMultiplexerOptions)

Controls multiplexed command execution.

```json
{
  "RedisMultiplexer": {
    "TransportProfile": "FullTilt",
    "Connections": 4,
    "MaxInFlightPerConnection": 4096,
    "ResponseTimeout": "00:00:02",
    "EnableCommandInstrumentation": true,
    "EnableCoalescedSocketWrites": true,
    "EnableSocketRespReader": false,
    "CoalescedWriteMaxBytes": 524288,
    "CoalescedWriteMaxSegments": 192,
    "CoalescedWriteSmallCopyThresholdBytes": 1536,
    "EnableAdaptiveCoalescing": true,
    "AdaptiveCoalescingLowDepth": 6,
    "AdaptiveCoalescingHighDepth": 56,
    "AdaptiveCoalescingMinWriteBytes": 65536,
    "AdaptiveCoalescingMinSegments": 48,
    "AdaptiveCoalescingMinSmallCopyThresholdBytes": 384
  }
}
```

**Notes**
- `EnableAutoscaling` and autoscaler thresholds are **Enterprise-only** operational controls.
- For OSS-only deployments, keep `EnableAutoscaling=false` (default).
- `ResponseTimeout` applies per command response; set to `00:00:00` to disable.
- Effective FullTilt profile sizing defaults are `CoalescedWriteMaxBytes=524288`, `CoalescedWriteMaxSegments=192`, `CoalescedWriteSmallCopyThresholdBytes=1536`.
- Coalesced write knobs control packet framing at the driver layer. Increase batch bytes/segments for throughput; reduce for lower tail latency.
- `EnableAdaptiveCoalescing=true` automatically scales between the adaptive minimum limits and configured max limits based on queue depth.
- `EnableSocketRespReader` is optional and defaults to `false`; enable only after validating in your environment.
- Set `TransportProfile=Custom` when you want explicit coalescing byte/segment values to win over profile defaults.
- Fast-path and lane-management diagrams: [MUX_FAST_PATH_ARCHITECTURE.md](MUX_FAST_PATH_ARCHITECTURE.md)

### Runtime Performance Guardrails

VapeCache now applies runtime normalization in infrastructure (`RedisRuntimeOptionsNormalizer`) after transport profile application.
This keeps baseline performance stable even when config values are invalid or extreme.

- Invalid host/port fall back to `localhost:6379`.
- Socket buffers are clamped to `4KB..4MB` (or `0` for OS defaults).
- Multiplexer batching/in-flight/autoscaler knobs are clamped to safe ranges.
- Negative/invalid time spans are normalized to safe defaults or `TimeSpan.Zero` where intended.

If normalization occurs, `RedisCommandExecutor` logs a warning so teams can correct config drift.

### Autoscaler Knobs (Enterprise)

```json
{
  "RedisMultiplexer": {
    "EnableAutoscaling": true,
    "MinConnections": 4,
    "MaxConnections": 16,
    "AutoscaleSampleInterval": "00:00:01",
    "ScaleUpWindow": "00:00:10",
    "ScaleDownWindow": "00:02:00",
    "ScaleUpCooldown": "00:00:20",
    "ScaleDownCooldown": "00:01:30",
    "ScaleUpInflightUtilization": 0.75,
    "ScaleDownInflightUtilization": 0.25,
    "ScaleUpQueueDepthThreshold": 32,
    "ScaleUpTimeoutRatePerSecThreshold": 2.0,
    "ScaleUpP99LatencyMsThreshold": 40.0,
    "ScaleDownP95LatencyMsThreshold": 20.0,
    "AutoscaleAdvisorMode": false,
    "EmergencyScaleUpTimeoutRatePerSecThreshold": 8.0,
    "ScaleDownDrainTimeout": "00:00:05",
    "MaxScaleEventsPerMinute": 2,
    "FlapToggleThreshold": 4,
    "AutoscaleFreezeDuration": "00:02:00",
    "ReconnectStormFailureRatePerSecThreshold": 2.0
  }
}
```

Detailed behavior, diagrams, and tuning guidance:
- [ENTERPRISE_MULTIPLEXER_AUTOSCALER.md](ENTERPRISE_MULTIPLEXER_AUTOSCALER.md)

## RedisCircuitBreaker (RedisCircuitBreakerOptions)

Controls hybrid failover behavior.

```json
{
  "RedisCircuitBreaker": {
    "Enabled": true,
    "ConsecutiveFailuresToOpen": 2,
    "BreakDuration": "00:00:10",
    "HalfOpenProbeTimeout": "00:00:00.250",
    "MaxConsecutiveRetries": 0,
    "UseExponentialBackoff": true,
    "MaxBreakDuration": "00:05:00",
    "MaxHalfOpenProbes": 5
  }
}
```

## HybridFailover (HybridFailoverOptions)

Controls how VapeCache keeps local in-memory fallback warm while Redis is healthy.

```json
{
  "HybridFailover": {
    "MirrorWritesToFallbackWhenRedisHealthy": true,
    "WarmFallbackOnRedisReadHit": true,
    "FallbackWarmReadTtl": "00:02:00",
    "FallbackMirrorWriteTtlWhenMissing": "00:05:00",
    "MaxMirrorPayloadBytes": 262144,
    "RemoveStaleFallbackOnRedisMiss": true
  }
}
```

**Notes**
- `MirrorWritesToFallbackWhenRedisHealthy=true` gives immediate failover continuity for recent writes.
- `WarmFallbackOnRedisReadHit=true` warms hot keys locally from Redis hits.
- `RemoveStaleFallbackOnRedisMiss=true` avoids stale local values during outages after Redis eviction.
- In multi-node/web-garden deployments, local in-memory fallback is still node-local. Use sticky sessions/affinity during failover.

## CacheStampede (CacheStampedeOptions)

Controls stampede protection.

```json
{
  "CacheStampede": {
    "Profile": "Balanced",
    "Enabled": true,
    "MaxKeys": 50000,
    "RejectSuspiciousKeys": true,
    "MaxKeyLength": 512,
    "LockWaitTimeout": "00:00:00.750",
    "EnableFailureBackoff": true,
    "FailureBackoff": "00:00:00.500"
  }
}
```

**Notes**
- `Profile` selects a preset baseline (`Strict`, `Balanced`, `Relaxed`) before manual overrides.
- `MaxKeys` bounds per-key lock cardinality to protect memory under key-flood scenarios.
- `LockWaitTimeout` defaults to `750ms` to protect p99 latency during stampedes.
- `FailureBackoff` defaults to `500ms` to avoid repeated origin hammering after factory failures.

## InMemorySpill (InMemorySpillOptions)

Controls large-payload spill behavior for the in-memory fallback cache.

```json
{
  "InMemorySpill": {
    "EnableSpillToDisk": false,
    "SpillThresholdBytes": 262144,
    "InlinePrefixBytes": 4096,
    "SpillDirectory": "%LOCALAPPDATA%/VapeCache/spill",
    "EnableOrphanCleanup": false,
    "OrphanCleanupInterval": "01:00:00",
    "OrphanMaxAge": "7.00:00:00"
  }
}
```

**Notes**
- Values larger than `SpillThresholdBytes` are stored with an in-memory prefix and a disk tail.
- `EnableSpillToDisk` requires a writable spill store registration (`AddVapeCachePersistence(...)`); otherwise fallback remains memory-only and emits diagnostics as `mode=noop`.
- Register a custom `ISpillEncryptionProvider` to encrypt spill files.
- Orphan cleanup is best-effort and only runs when enabled.

## Redis Reconciliation (Optional)

Reconciliation lives in the `VapeCache.Reconciliation` package. It tracks in-memory writes during outages and replays them when Redis recovers.

```json
{
  "RedisReconciliation": {
    "Enabled": true,
    "MaxOperationAge": "00:05:00",
    "MaxPendingOperations": 100000,
    "MaxOperationsPerRun": 10000,
    "BatchSize": 256,
    "MaxRunDuration": "00:00:30",
    "InitialBackoff": "00:00:00.025",
    "MaxBackoff": "00:00:02",
    "BackoffMultiplier": 2.0
  },
  "RedisReconciliationStore": {
    "UseSqlite": true,
    "StorePath": "%LOCALAPPDATA%/VapeCache/persistence/reconciliation.db",
    "BusyTimeoutMs": 1000,
    "EnablePragmaOptimizations": true,
    "VacuumOnClear": false
  }
}
```

Enable in code:

```csharp
builder.Services.AddVapeCacheRedisReconciliation(options =>
{
    options.MaxOperationAge = TimeSpan.FromMinutes(5);
});
```

## ASP.NET Core Output Caching (MVC/Blazor/Minimal API)

Use `VapeCache.Extensions.AspNetCore` to keep ASP.NET Core output-cache middleware/policies while storing responses in VapeCache:

```csharp
builder.Services.AddVapeCacheOutputCaching(
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
```

Sticky-session affinity hints for clustered/web-garden hosts:

```csharp
builder.Services.AddVapeCacheFailoverAffinityHints(options =>
{
    options.NodeId = Environment.MachineName;
    options.CookieName = "VapeCacheAffinity";
});

var app = builder.Build();
app.UseVapeCacheFailoverAffinityHints();
```

Configuration binding option:

```csharp
builder.Services.AddVapeCacheOutputCaching(builder.Configuration);
```

```json
{
  "VapeCacheOutputCache": {
    "KeyPrefix": "vapecache:output",
    "DefaultTtl": "00:00:30",
    "EnableTagIndexing": true
  }
}
```

## Enterprise Licensing Runtime

Enterprise extension gates now fail closed on missing keys. Set a key explicitly:

```bash
$env:VAPECACHE_LICENSE_KEY = "VC2...."
```

Optional online revocation/kill-switch checks:

```bash
$env:VAPECACHE_LICENSE_REVOCATION_ENABLED = "true"
$env:VAPECACHE_LICENSE_REVOCATION_ENDPOINT = "https://license-control-plane.internal"
$env:VAPECACHE_LICENSE_REVOCATION_API_KEY = "<secret>"
$env:VAPECACHE_LICENSE_REVOCATION_TIMEOUT_MS = "2000"
$env:VAPECACHE_LICENSE_REVOCATION_CACHE_SECONDS = "60"
```

Online revocation now fails closed by default. Only set `VAPECACHE_LICENSE_REVOCATION_FAIL_OPEN=true` if you explicitly want legacy fail-open behavior during revocation endpoint failures.

Verifier override hardening:

- Default behavior ignores verifier env overrides.
- To opt in (dev/test only), set:
  - `$env:VAPECACHE_LICENSE_ALLOW_VERIFIER_ENV_OVERRIDE = "true"`

## Environment Variables

Every option can be overridden via environment variables using `__` as the separator:

```bash
$env:RedisConnection__Host = "prod-redis.example.com"
$env:RedisConnection__Password = "secret"
$env:RedisCircuitBreaker__ConsecutiveFailuresToOpen = "3"
```

Connection string override:

```bash
$env:VAPECACHE_REDIS_CONNECTIONSTRING = "redis://admin:secret@redis.example.com:6379/0"
```

## Programmatic Configuration

```csharp
builder.Services.Configure<RedisConnectionOptions>(options =>
{
    options.MaxConnections = 128;
});

builder.Services.Configure<RedisCircuitBreakerOptions>(options =>
{
    options.BreakDuration = TimeSpan.FromSeconds(5);
});

builder.Services.AddOptions<CacheStampedeOptions>()
    .UseCacheStampedeProfile(CacheStampedeProfile.Balanced)
    .ConfigureCacheStampede(options =>
    {
        options.WithLockWaitTimeout(TimeSpan.FromMilliseconds(600))
            .WithFailureBackoff(TimeSpan.FromMilliseconds(400));
    });
```

`UseCacheStampedeProfile(...)` sets the baseline, then `ConfigureCacheStampede(...)` applies code overrides. If you also `Bind(...)` from configuration, place `Bind(...)` last when you want appsettings/environment to win.

## See Also
- [QUICKSTART.md](QUICKSTART.md)
- [ARCHITECTURE.md](ARCHITECTURE.md)
- [MUX_FAST_PATH_ARCHITECTURE.md](MUX_FAST_PATH_ARCHITECTURE.md)
- [OBSERVABILITY_ARCHITECTURE.md](OBSERVABILITY_ARCHITECTURE.md)
- [ENTERPRISE_MULTIPLEXER_AUTOSCALER.md](ENTERPRISE_MULTIPLEXER_AUTOSCALER.md)
