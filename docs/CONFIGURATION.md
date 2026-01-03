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

## RedisMultiplexer (RedisMultiplexerOptions)

Controls multiplexed command execution.

```json
{
  "RedisMultiplexer": {
    "Connections": 4,
    "MaxInFlightPerConnection": 4096,
    "ResponseTimeout": "00:00:02",
    "EnableCommandInstrumentation": true,
    "EnableCoalescedSocketWrites": true
  }
}
```

**Notes**
- `ResponseTimeout` applies per command response; set to `00:00:00` to disable.

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
    "Enabled": true,
    "MaxKeys": 100000
  }
}
```

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
```

## See Also
- [QUICKSTART.md](QUICKSTART.md)
- [ARCHITECTURE.md](ARCHITECTURE.md)
- [OBSERVABILITY_ARCHITECTURE.md](OBSERVABILITY_ARCHITECTURE.md)
