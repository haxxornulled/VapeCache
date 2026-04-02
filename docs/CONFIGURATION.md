# VapeCache Configuration

VapeCache uses the `IOptions<T>` pattern. Configuration is owned by the host (appsettings/environment variables), and VapeCache consumes options via DI.

This guide is the practical setup document. For the exhaustive property-by-property inventory, defaults, and generated source-of-truth, use:

- [SETTINGS_REFERENCE.md](SETTINGS_REFERENCE.md)

For logging/telemetry-specific keys and fallback behavior, use:

- [LOGGING_TELEMETRY_CONFIGURATION.md](LOGGING_TELEMETRY_CONFIGURATION.md)

## Choose a Runtime Mode

Most applications should start with one of these two registrations:

| Registration | Redis required | Auto-bound sections | Use when |
|---|---|---|---|
| `AddVapeCache(configuration)` | Yes | `RedisConnection`, `RedisMultiplexer`, `RedisCircuitBreaker`, `HybridFailover`, `CacheStampede`, `InMemorySpill` | You want the full Redis-first hybrid runtime. |
| `AddVapeCacheInMemory(configuration)` | No | `CacheStampede`, `InMemorySpill` | You want a local-only runtime for dev, tests, or lightweight single-node hosts. |

Important behavior:

- `AddVapeCache(...)` is the normal production path. It validates Redis configuration on startup.
- `AddVapeCacheInMemory(...)` is an explicit mode, not a degraded fallback. It does not register Redis and intentionally ignores Redis-only sections.
- In-memory-only mode keeps stampede protection, tags/zones, typed collections, and the `IDistributedCache` bridge, but it does not expose Redis-specific breaker/failover behavior.

## Minimal Setup

### Hybrid Redis Mode

Smallest useful appsettings:

```json
{
  "RedisConnection": {
    "Host": "localhost"
  }
}
```

Recommended registration:

```csharp
using VapeCache.Extensions.DependencyInjection;

builder.Services.AddVapeCache(builder.Configuration);
```

You can also override the endpoint with a single environment variable:

```bash
$env:VAPECACHE_REDIS_CONNECTIONSTRING = "redis://localhost:6379/0"
```

`VAPECACHE_REDIS_CONNECTIONSTRING` is a runtime override for the Redis endpoint and is only relevant in hybrid Redis mode.

### In-Memory-Only Mode

Smallest useful appsettings:

```json
{
  "InMemorySpill": {
    "MemoryCacheSizeLimitBytes": 268435456
  }
}
```

Recommended registration:

```csharp
using VapeCache.Extensions.DependencyInjection;

builder.Services.AddVapeCacheInMemory(builder.Configuration);
```

In-memory-only mode intentionally ignores Redis-only sections such as:

- `RedisConnection`
- `RedisMultiplexer`
- `RedisCircuitBreaker`
- `HybridFailover`

It still binds and uses:

- `CacheStampede`
- `InMemorySpill`

### Low-Level Registration Without The DI Facade

If you want to wire the core runtime manually instead of using `AddVapeCache(...)`, bind the sections you need yourself and then register the lower-level services:

```csharp
using VapeCache.Abstractions.Connections;

builder.Services.AddOptions<RedisConnectionOptions>()
    .Bind(builder.Configuration.GetSection("RedisConnection"));

builder.Services.AddVapecacheRedisConnections();
builder.Services.AddVapecacheCaching();
```

That approach gives you exact control, but the DI facade is the recommended entrypoint for most apps.

## What The DI Facade Binds

`VapeCacheDependencyInjectionBuilder.BindFromConfiguration(...)` controls section binding for the DI facade. By default:

- `AddVapeCache(configuration)` binds all core runtime sections.
- `AddVapeCacheInMemory(configuration)` binds only memory-local sections and force-disables the Redis/hybrid ones.

The binder switches live on `VapeCacheConfigurationBindingOptions`:

| Option | Default | Meaning |
|---|---|---|
| `RedisConnectionSectionName` | `RedisConnection` | Section name for `RedisConnectionOptions`. |
| `RedisMultiplexerSectionName` | `RedisMultiplexer` | Section name for `RedisMultiplexerOptions`. |
| `RedisCircuitBreakerSectionName` | `RedisCircuitBreaker` | Section name for `RedisCircuitBreakerOptions`. |
| `HybridFailoverSectionName` | `HybridFailover` | Section name for `HybridFailoverOptions`. |
| `CacheStampedeSectionName` | `CacheStampede` | Section name for `CacheStampedeOptions`. |
| `InMemorySpillSectionName` | `InMemorySpill` | Section name for `InMemorySpillOptions`. |
| `BindRedisConnection` | `true` | Whether to bind Redis connection options. |
| `BindRedisMultiplexer` | `true` | Whether to bind Redis multiplexer options. |
| `BindRedisCircuitBreaker` | `true` | Whether to bind circuit-breaker options. |
| `BindHybridFailover` | `true` | Whether to bind hybrid failover options. |
| `BindCacheStampede` | `true` | Whether to bind stampede options. |
| `BindInMemorySpill` | `true` | Whether to bind memory spill options. |

Example: rename sections to fit an existing host config shape:

```csharp
builder.Services.AddVapeCache(builder.Configuration, options =>
{
    options.RedisConnectionSectionName = "Caching:Redis";
    options.RedisMultiplexerSectionName = "Caching:Multiplexer";
    options.CacheStampedeSectionName = "Caching:Stampede";
});
```

Example: keep the hybrid runtime but opt out of binding one section because you want to configure it purely in code:

```csharp
builder.Services.AddVapeCache(builder.Configuration, options =>
{
    options.BindRedisMultiplexer = false;
});
```

```csharp
builder.Services.Configure<RedisMultiplexerOptions>(options =>
{
    options.Connections = 8;
    options.ResponseTimeout = TimeSpan.FromSeconds(1);
});
```

For `AddVapeCacheInMemory(configuration)`, the Redis/hybrid bind flags are always forced off even if a callback tries to re-enable them.

## Optional Package Sections

Not every configuration section is consumed by `AddVapeCache(...)`. Optional packages/extensions bind their own sections:

| Section | Registered by | Purpose |
|---|---|---|
| `RedisPubSub` | `AddVapeCachePubSub(configuration)` | Redis pub/sub delivery behavior. |
| `VapeCache:Search` | `AddVapeCacheSearch(configuration)` | HASH-backed RediSearch projection/search behavior. |
| `VapeCacheOutputCache` | `AddVapeCacheOutputCaching(configuration)` | ASP.NET Core output-cache store settings. |
| `RedisReconciliation` | `AddVapeCacheRedisReconciliation(...)` | Post-outage write replay behavior. |
| `RedisReconciliationStore` | reconciliation package registration | Durable reconciliation store settings. |

If a section is not taking effect, the first thing to check is whether the extension package that owns it was actually registered.

`VapeCache:Search` currently exposes:

- `Enabled`
- `RequireModuleAvailability`
- `DefaultResultCount`

That section is only consumed by `VapeCache.Features.Search`.

## Safe Connection String Building (Junior-Friendly)

If you need to build a Redis URI in code, use `RedisConnectionStringBuilder` instead of hand-writing strings.
If your host is Autofac-based, you can resolve `IRedisConnectionStringBuilder` from the container.

```csharp
using VapeCache.Abstractions.Connections;

var options = new RedisConnectionOptions
{
    Host = "redis.internal",
    Port = 6380,
    Database = 0,
    Username = "svc-cache",
    Password = "secret",
    UseTls = true,
    TlsHost = "redis.internal"
};

var builder = new RedisConnectionStringBuilder();
var connectionString = builder.Build(options);
// rediss://svc-cache:secret@redis.internal:6380/0?tls=true&sni=redis.internal
```

Rules enforced by the builder:

- `Host` must be host-only (no `redis://` prefix and no `:port` suffix).
- `Username` requires a non-empty `Password`.
- `TlsHost` and `AllowInvalidCert` require `UseTls=true`.
- Raw IPv6 hosts are normalized to bracketed authority format.

This same builder is now used in the runtime connection factory to validate/canonicalize the effective connection endpoint before socket connect.

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
    "TransportProfile": "FullTilt",
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
- This section answers three questions: where Redis lives, how large the pooled connection budget is, and how aggressive the socket/runtime tuning should be.
- At startup, VapeCache requires either `RedisConnection:Host` or `RedisConnection:ConnectionString`.
- `ConnectionString` overrides host/port/user/password/database when set.
- Use `ConnectionString` when secrets come from Key Vault, environment variables, or secret stores and you want one opaque value instead of separate fields.
- `MaxConnections`, `MaxIdle`, and `Warm` are the pool-budget knobs. `Warm` should be a small eager subset of `MaxIdle`, not the full pool size.
- `ConnectTimeout` and `AcquireTimeout` shape startup/connect behavior and lease pressure. Increase them only when network RTT or server load justifies it.
- `AllowInvalidCert=true` is blocked in production environments.
- `MaxConnections` and `MaxIdle` drive the connection pool size.
- Defaults are full-tilt for high-throughput links (`TcpSendBufferBytes=4MB`, `TcpReceiveBufferBytes=4MB`).
- `EnableTcpNoDelay=true` is best for low-latency cache workloads.
- `TcpSendBufferBytes` / `TcpReceiveBufferBytes` let you tune socket buffering for high-throughput links.
- `TransportProfile` is the high-level preset for transport tuning. Stay on `FullTilt` unless you are intentionally biasing for a different latency/throughput profile.
- `RespProtocolVersion` supports `2` or `3`; default negotiation is `RESP2` unless configured.
- `MaxBulkStringBytes` and `MaxArrayDepth` are parser safety rails, not performance knobs. Leave them alone unless you have an explicit payload/response reason.
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
    "EnableCommandInstrumentation": false,
    "EnableCoalescedSocketWrites": true,
    "EnableSocketRespReader": false,
    "UseDedicatedLaneWorkers": false,
    "CoalescedWriteMaxBytes": 524288,
    "CoalescedWriteMaxSegments": 192,
    "CoalescedWriteSmallCopyThresholdBytes": 1536,
    "EnableAdaptiveCoalescing": true,
    "AdaptiveCoalescingLowDepth": 6,
    "AdaptiveCoalescingHighDepth": 56,
    "AdaptiveCoalescingMinWriteBytes": 65536,
    "AdaptiveCoalescingMinSegments": 48,
    "AdaptiveCoalescingMinSmallCopyThresholdBytes": 384,
    "CoalescingEnterQueueDepth": 8,
    "CoalescingExitQueueDepth": 3,
    "CoalescedWriteMaxOperations": 128,
    "CoalescingSpinBudget": 8,
    "AutoAdjustBulkLanes": false,
    "BulkLaneConnections": 1,
    "BulkLaneTargetRatio": 0.25,
    "BulkLaneResponseTimeout": "00:00:05",
    "PubSubLaneConnections": 0,
    "BlockingLaneConnections": 0
  }
}
```

**Notes**
- `RedisMultiplexer` is the advanced transport section. Most teams only change `Connections`, `ResponseTimeout`, `EnableCommandInstrumentation`, and maybe the lane-isolation settings.
- `EnableAutoscaling` and autoscaler thresholds are **Enterprise-only** operational controls.
- For OSS-only deployments, keep `EnableAutoscaling=false` (default).
- `Connections` is the total mux lane budget. When you reserve bulk, pub/sub, or blocking lanes, at least one fast lane must remain.
- `EnableCommandInstrumentation=false` is the default and is recommended for strict zero-allocation hot paths.
- `ResponseTimeout` applies per command response; set to `00:00:00` to disable.
- Effective FullTilt profile sizing defaults are `CoalescedWriteMaxBytes=524288`, `CoalescedWriteMaxSegments=192`, `CoalescedWriteSmallCopyThresholdBytes=1536`.
- Coalesced write knobs control packet framing at the driver layer. Increase batch bytes/segments for throughput; reduce for lower tail latency.
- `EnableAdaptiveCoalescing=true` automatically scales between the adaptive minimum limits and configured max limits based on queue depth.
- `EnableSocketRespReader` is optional and defaults to `false`; enable only after validating in your environment.
- `UseDedicatedLaneWorkers` is a specialized contention-control knob for sustained extreme load. Leave it off unless you have profiled thread-pool pressure.
- Set `TransportProfile=Custom` when you want explicit coalescing byte/segment values to win over profile defaults.
- `CoalescingEnterQueueDepth` / `CoalescingExitQueueDepth` provide burst hysteresis so short spikes batch well without holding lone requests.
- `CoalescedWriteMaxOperations` limits per-batch op count for predictable p95/p99 behavior.
- `CoalescingSpinBudget` controls brief follower capture during bursts; keep this small for stable tails.
- `BulkLaneConnections` isolates pooled bulk paths from fast-lane p99 and autoscaler signals. Use `0` to disable isolation and share fast lanes.
- `AutoAdjustBulkLanes=true` makes `BulkLaneTargetRatio` the controlling knob instead of fixed `BulkLaneConnections`.
- `PubSubLaneConnections` and `BlockingLaneConnections` reserve lanes for those workloads so request/response traffic does not fight with them.
- Fast-path and lane-management diagrams: [MUX_FAST_PATH_ARCHITECTURE.md](MUX_FAST_PATH_ARCHITECTURE.md)

### Runtime Performance Guardrails

VapeCache now applies runtime normalization in infrastructure (`RedisRuntimeOptionsNormalizer`) after transport profile application.
This keeps baseline performance stable even when config values are invalid or extreme.

- Invalid host/port fall back to `localhost:6379`.
- Socket buffers are clamped to `4KB..4MB` (or `0` for OS defaults).
- Multiplexer batching/in-flight/autoscaler knobs are clamped to safe ranges.
- Negative/invalid time spans are normalized to safe defaults or `TimeSpan.Zero` where intended.

If normalization occurs, `RedisCommandExecutor` logs a warning so teams can correct config drift.

### Runtime Snapshot + Hot Reload Behavior

`RedisCommandExecutor` applies multiplexer options through an immutable runtime snapshot that is swapped atomically on `IOptionsMonitor` updates.

- Hot paths read a single snapshot reference per decision point to avoid mixed-config reads.
- `EnableCommandInstrumentation` updates take effect immediately without transport restarts.
- New lanes created during autoscale events use the latest snapshot defaults.
- Existing long-lived transport loops keep their current socket/runtime state until naturally recycled.

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
    "ReconnectStormFailureRatePerSecThreshold": 2.0,
    "EnableSpillPressureSignals": true,
    "SpillPressureTotalFilesThreshold": 4000,
    "SpillPressureActiveShardsThreshold": 48,
    "SpillPressureImbalanceRatioThreshold": 1.75,
    "SpillPressureSustainedWindow": "00:00:20"
  }
}
```

Detailed behavior, diagrams, and tuning guidance:
- [ENTERPRISE_MULTIPLEXER_AUTOSCALER.md](ENTERPRISE_MULTIPLEXER_AUTOSCALER.md)
- [MUX_MAINTAINER_GUIDE.md](MUX_MAINTAINER_GUIDE.md)
- [MUX_PR_REVIEW_CHECKLIST.md](MUX_PR_REVIEW_CHECKLIST.md)
- Complete options inventory: [SETTINGS_REFERENCE.md](SETTINGS_REFERENCE.md)

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

**Notes**
- This section only matters in hybrid Redis mode. It is not used by `AddVapeCacheInMemory(...)`.
- `ConsecutiveFailuresToOpen` and `BreakDuration` are the main sensitivity knobs. Lower values fail over faster; higher values tolerate more transient Redis noise before opening.
- `MaxConsecutiveRetries=0` means Redis recovery probes continue indefinitely.
- `UseExponentialBackoff=true` is the safer default for unstable infrastructure because it prevents hot retry loops against a sick backend.

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
- This section only applies to the hybrid Redis runtime. In-memory-only mode has no Redis primary to mirror from.
- `MirrorWritesToFallbackWhenRedisHealthy=true` gives immediate failover continuity for recent writes.
- `WarmFallbackOnRedisReadHit=true` warms hot keys locally from Redis hits.
- `RemoveStaleFallbackOnRedisMiss=true` avoids stale local values during outages after Redis eviction.
- In multi-node/web-garden deployments, local in-memory fallback is still node-local. Use sticky sessions/affinity during failover.

## CacheStampede (CacheStampedeOptions)

Controls stampede protection.

```json
{
  "CacheStampede": {
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
- `CacheStampedeOptions` does not expose a bindable `Profile` property. To apply a preset baseline, use `WithCacheStampedeProfile(...)` or `UseCacheStampedeProfile(...)` in code, then optionally override individual settings through configuration.
- `MaxKeys` bounds per-key lock cardinality to protect memory under key-flood scenarios.
- `LockWaitTimeout` defaults to `750ms` to protect p99 latency during stampedes.
- `FailureBackoff` defaults to `500ms` to avoid repeated origin hammering after factory failures.

## InMemorySpill (InMemorySpillOptions)

Controls large-payload spill behavior for the in-memory fallback cache and the in-memory-only runtime mode.

```json
{
  "InMemorySpill": {
    "MemoryCacheSizeLimitBytes": 536870912,
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
- `MemoryCacheSizeLimitBytes` sets a hard cap (bytes) for the in-memory fallback cache or memory-only runtime. Set `0` for default unbounded behavior.
- Values larger than `SpillThresholdBytes` are stored with an in-memory prefix and a disk tail.
- `EnableSpillToDisk` requires a writable spill store registration (`AddVapeCachePersistence(...)`); otherwise fallback remains memory-only and emits diagnostics as `mode=noop`.
- Register a custom `ISpillEncryptionProvider` to encrypt spill files.
- Orphan cleanup is best-effort and only runs when enabled.

## Redis Pub/Sub (Optional)

Pub/sub lives in the `VapeCache.Extensions.PubSub` package and binds through `AddVapeCachePubSub(configuration)`.

```json
{
  "RedisPubSub": {
    "Enabled": true,
    "DeliveryQueueCapacity": 512,
    "DropOldestOnBackpressure": true,
    "ReconnectDelayMin": "00:00:00.250",
    "ReconnectDelayMax": "00:00:05"
  }
}
```

Enable in code:

```csharp
builder.Services.AddVapeCache(builder.Configuration);
builder.Services.AddVapeCachePubSub(builder.Configuration);
```

**Notes**
- `DeliveryQueueCapacity` is the main pressure knob. Increase it when subscribers legitimately need more buffering; reduce it when you want earlier backpressure.
- `DropOldestOnBackpressure=true` favors freshness. Set it to `false` when newest-message preservation matters more than recency.
- `ReconnectDelayMin` and `ReconnectDelayMax` control the reconnect backoff envelope after subscriber connection failures.
- If you use a non-default section name, call `AddVapeCachePubSub(configuration, sectionName: "...")`.

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

**Notes**
- Reconciliation is opt-in and package-specific. Register it explicitly; it is not turned on by `AddVapeCache(...)` alone.
- This is useful when you want memory-side writes during outages to be replayed back into Redis after recovery.
- `MaxPendingOperations`, `MaxOperationsPerRun`, and `BatchSize` are the main capacity/backpressure knobs.

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

**Notes**
- `KeyPrefix` should be treated like namespace ownership for HTTP response entries. Change it when multiple apps share the same backend and you want clean separation.
- `DefaultTtl` only applies when the output-cache middleware asks for a non-positive duration.
- `EnableTagIndexing=true` is what allows `EvictByTag` scenarios. Leave it on unless you know you do not need tag-based eviction.

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

Enterprise gate integration hooks:

```csharp
// Microsoft DI
builder.Services.AddVapeCache()
    .UseEnterpriseFeatureGate<MyEnterpriseFeatureGate>();
```

```csharp
// Autofac
var containerBuilder = new ContainerBuilder();
containerBuilder.RegisterModule(new VapeCacheCachingModule());
containerBuilder.RegisterVapeCacheEnterpriseFeatureGate<MyEnterpriseFeatureGate>();
```

## Environment Variables

Every option can be overridden via environment variables using `__` as the separator:

```bash
$env:RedisConnection__Host = "prod-redis.example.com"
$env:RedisConnection__Password = "secret"
$env:RedisCircuitBreaker__ConsecutiveFailuresToOpen = "3"
$env:InMemorySpill__MemoryCacheSizeLimitBytes = "536870912"
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

For the in-memory-only runtime, configure `CacheStampedeOptions` and `InMemorySpillOptions` the same way, but do not depend on `RedisConnectionOptions` being present.

`UseCacheStampedeProfile(...)` sets the baseline, then `ConfigureCacheStampede(...)` applies code overrides. If you also `Bind(...)` from configuration, place `Bind(...)` last when you want appsettings/environment to win.

## See Also
- [QUICKSTART.md](QUICKSTART.md)
- [ARCHITECTURE.md](ARCHITECTURE.md)
- [MUX_FAST_PATH_ARCHITECTURE.md](MUX_FAST_PATH_ARCHITECTURE.md)
- [OBSERVABILITY_ARCHITECTURE.md](OBSERVABILITY_ARCHITECTURE.md)
- [ENTERPRISE_MULTIPLEXER_AUTOSCALER.md](ENTERPRISE_MULTIPLEXER_AUTOSCALER.md)
