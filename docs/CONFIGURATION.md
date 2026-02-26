# VapeCache Configuration

VapeCache uses the `IOptions<T>` pattern. Configuration is owned by the host (appsettings/environment variables), and VapeCache consumes options via DI.

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
    "AllowAuthFallbackToPasswordOnly": true,
    "LogWhoAmIOnConnect": false,
    "MaxBulkStringBytes": 16777216,
    "MaxArrayDepth": 64
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

## RedisMultiplexer (RedisMultiplexerOptions)

Controls multiplexed command execution.

```json
{
  "RedisMultiplexer": {
    "Connections": 4,
    "MaxInFlightPerConnection": 4096,
    "ResponseTimeout": "00:00:02",
    "EnableCommandInstrumentation": true,
    "EnableCoalescedSocketWrites": true,
    "EnableSocketRespReader": false,
    "CoalescedWriteMaxBytes": 1048576,
    "CoalescedWriteMaxSegments": 256,
    "CoalescedWriteSmallCopyThresholdBytes": 2048,
    "EnableAdaptiveCoalescing": true,
    "AdaptiveCoalescingLowDepth": 4,
    "AdaptiveCoalescingHighDepth": 64,
    "AdaptiveCoalescingMinWriteBytes": 65536,
    "AdaptiveCoalescingMinSegments": 64,
    "AdaptiveCoalescingMinSmallCopyThresholdBytes": 512
  }
}
```

**Notes**
- `EnableAutoscaling` and autoscaler thresholds are **Enterprise-only** operational controls.
- For OSS-only deployments, keep `EnableAutoscaling=false` (default).
- `ResponseTimeout` applies per command response; set to `00:00:00` to disable.
- Defaults are full-tilt (`CoalescedWriteMaxBytes=1MB`, `CoalescedWriteMaxSegments=256`, `CoalescedWriteSmallCopyThresholdBytes=2048`).
- Coalesced write knobs control packet framing at the driver layer. Increase batch bytes/segments for throughput; reduce for lower tail latency.
- `EnableAdaptiveCoalescing=true` automatically scales between the adaptive minimum limits and configured max limits based on queue depth.
- `EnableSocketRespReader` is optional and defaults to `false`; enable only after validating in your environment.

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
    "EnableSpillToDisk": true,
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
- [OBSERVABILITY_ARCHITECTURE.md](OBSERVABILITY_ARCHITECTURE.md)
- [ENTERPRISE_MULTIPLEXER_AUTOSCALER.md](ENTERPRISE_MULTIPLEXER_AUTOSCALER.md)
