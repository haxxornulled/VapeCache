# Redis Circuit Breaker Configuration Guide

## Overview

VapeCache includes a production-ready circuit breaker pattern that automatically fails over to in-memory mode when Redis becomes unavailable. This provides **zero-downtime resilience** and an excellent developer experience with fast failure detection and configurable retry behavior.

## Table of Contents

- [Quick Start](#quick-start)
- [How It Works](#how-it-works)
- [Configuration Options](#configuration-options)
- [Retry Strategies](#retry-strategies)
- [Reconciliation](#reconciliation)
- [Common Scenarios](#common-scenarios)
- [Monitoring and Observability](#monitoring-and-observability)
- [Advanced Configuration](#advanced-configuration)
- [Troubleshooting](#troubleshooting)

---

## Quick Start

### Minimal Configuration (Recommended Defaults)

Add this to your `appsettings.json`:

```json
{
  "RedisCircuitBreaker": {
    "Enabled": true,
    "ConsecutiveFailuresToOpen": 2,
    "BreakDuration": "00:00:10",
    "HalfOpenProbeTimeout": "00:00:00.250",
    "MaxConsecutiveRetries": 0,
    "UseExponentialBackoff": true,
    "MaxBreakDuration": "00:05:00"
  }
}
```

This configuration:
- ✅ Fails over to in-memory mode after **2 connection failures** (~1 second)
- ✅ Retries Redis connection every **10 seconds** initially
- ✅ Uses **exponential backoff** (10s → 20s → 40s → 80s → 160s → 300s max)
- ✅ **Never gives up** on Redis recovery (infinite retries)
- ✅ Provides visible console output with retry counts

---

## How It Works

### Circuit States

The circuit breaker operates in three states:

```
┌─────────────┐
│   CLOSED    │  Normal operation - Redis is healthy
│  (Normal)   │  All requests go to Redis
└─────┬───────┘
      │
      │ 2+ failures within 2 seconds
      ▼
┌─────────────┐
│    OPEN     │  Redis is unhealthy
│ (Fallback)  │  All requests go to IN-MEMORY cache
└─────┬───────┘  Waits for BreakDuration before testing
      │
      │ BreakDuration elapsed
      ▼
┌─────────────┐
│ HALF-OPEN   │  Testing Redis recovery
│  (Probing)  │  One test connection attempt
└─────┬───────┘
      │
      ├─ Success ──────────┐
      │                    ▼
      │              ┌─────────────┐
      │              │   CLOSED    │
      │              │  (Recovered)│
      │              └─────────────┘
      │
      └─ Failure ──────────┐
                           ▼
                     ┌─────────────┐
                     │    OPEN     │
                     │ (Retry #N)  │
                     └─────────────┘
                     BreakDuration doubles (if exponential backoff enabled)
```

### Failure Detection

The circuit breaker monitors **connection-level failures**:

1. **Fast Detection**: 2-second sampling window captures failures quickly
2. **Threshold**: Opens circuit after `ConsecutiveFailuresToOpen` failures (default: 2)
3. **Failure Types**: Connection timeouts, authentication failures, network errors
4. **Immediate Fallback**: Switches to in-memory mode within ~1 second

### Visual Feedback

When the circuit breaker activates, you'll see clear console output:

```
🔥🔥🔥 ⚡ CIRCUIT BREAKER OPENED! Redis connections failing (retry #1), switching to IN-MEMORY mode for 10 seconds 🔥🔥🔥

🔄 Circuit breaker HALF-OPEN. Testing Redis connection...

✅✅✅ Circuit breaker CLOSED! Redis operations resumed after 3 retries. ✅✅✅
```

---

## Configuration Options

### `Enabled`

**Type**: `bool`
**Default**: `true`
**Description**: Master switch for circuit breaker functionality.

```json
{
  "RedisCircuitBreaker": {
    "Enabled": false  // Disables circuit breaker - connections fail without fallback
  }
}
```

**When to use**:
- Set to `false` in production if you want strict Redis-only mode (no fallback)
- Keep `true` for development and most production scenarios

---

### `ConsecutiveFailuresToOpen`

**Type**: `int`
**Default**: `2`
**Minimum**: `2` (Polly framework constraint)
**Description**: Number of consecutive connection failures before opening the circuit.

```json
{
  "RedisCircuitBreaker": {
    "ConsecutiveFailuresToOpen": 2  // Open after 2 failures (~1 second)
  }
}
```

**Impact on failure detection time**:
- `2`: ~1.0 seconds (2 × 500ms connection timeout)
- `3`: ~1.5 seconds (3 × 500ms connection timeout)
- `5`: ~2.5 seconds (5 × 500ms connection timeout)

**Recommendation**: Use `2` for fast failover in development and most production scenarios.

---

### `BreakDuration`

**Type**: `TimeSpan`
**Default**: `00:00:10` (10 seconds)
**Description**: **Initial** duration to keep the circuit open before attempting a half-open probe.

```json
{
  "RedisCircuitBreaker": {
    "BreakDuration": "00:00:10"  // Start with 10-second retry interval
  }
}
```

**With exponential backoff** (recommended):
- Retry #1: 10 seconds
- Retry #2: 20 seconds
- Retry #3: 40 seconds
- Retry #4: 80 seconds
- Retry #5: 160 seconds
- Retry #6+: 300 seconds (capped at `MaxBreakDuration`)

**Without exponential backoff**:
- All retries: 10 seconds (constant)

**Recommendations**:
- **Development**: `00:00:05` to `00:00:10` (5-10 seconds)
- **Production**: `00:00:10` to `00:00:30` (10-30 seconds)
- **High-availability**: `00:00:05` (5 seconds) with exponential backoff

---

### `HalfOpenProbeTimeout`

**Type**: `TimeSpan`
**Default**: `00:00:00.250` (250 milliseconds)
**Description**: Maximum time to wait for a half-open probe connection attempt.

```json
{
  "RedisCircuitBreaker": {
    "HalfOpenProbeTimeout": "00:00:00.250"  // 250ms probe timeout
  }
}
```

**Purpose**: Prevents probe attempts from hanging indefinitely if Redis is still struggling.

**Recommendations**:
- **Fast networks**: `00:00:00.100` to `00:00:00.250` (100-250ms)
- **Slow networks**: `00:00:00.500` to `00:00:01.000` (500ms-1s)
- **Cloud/distributed**: `00:00:00.500` (500ms)

---

### `MaxConsecutiveRetries`

**Type**: `int`
**Default**: `0` (infinite retries)
**Description**: Maximum number of consecutive retry attempts before giving up completely.

```json
{
  "RedisCircuitBreaker": {
    "MaxConsecutiveRetries": 0  // Never give up (infinite retries)
  }
}
```

**Behavior**:
- `0`: **Infinite retries** - Circuit will keep trying forever until Redis recovers
- `> 0`: After N failed retries, circuit stays **permanently open** until application restart

**Examples**:

```json
// Infinite retries (recommended for most scenarios)
{
  "MaxConsecutiveRetries": 0
}

// Give up after 10 retries (~17 minutes with exponential backoff)
{
  "MaxConsecutiveRetries": 10
}

// Give up after 5 retries (~5 minutes with exponential backoff)
{
  "MaxConsecutiveRetries": 5
}
```

**Recommendations**:
- **Development**: `0` (infinite) - always keep trying
- **Production (auto-scaling)**: `10-20` - let orchestrator restart the pod
- **Production (single instance)**: `0` (infinite) - keep the app running
- **Scheduled maintenance**: `0` (infinite) - recover automatically when Redis comes back

---

### `UseExponentialBackoff`

**Type**: `bool`
**Default**: `true`
**Description**: Whether to double the break duration after each failed retry.

```json
{
  "RedisCircuitBreaker": {
    "UseExponentialBackoff": true  // Recommended
  }
}
```

**Impact**:

| Retry # | With Backoff | Without Backoff |
|---------|--------------|-----------------|
| 1       | 10s          | 10s             |
| 2       | 20s          | 10s             |
| 3       | 40s          | 10s             |
| 4       | 80s          | 10s             |
| 5       | 160s         | 10s             |
| 6       | 300s (max)   | 10s             |

**Benefits of exponential backoff**:
- ✅ Reduces load on struggling Redis instance
- ✅ Prevents thundering herd during recovery
- ✅ Balanced between fast recovery and system stability
- ✅ Industry-standard retry pattern

**Recommendations**:
- **Production**: `true` (always use exponential backoff)
- **Development**: `true` (matches production behavior)
- **Testing**: `false` (constant retries for predictable test timing)

---

### `MaxBreakDuration`

**Type**: `TimeSpan`
**Default**: `00:05:00` (5 minutes)
**Description**: Maximum break duration when using exponential backoff. Prevents infinite growth.

```json
{
  "RedisCircuitBreaker": {
    "MaxBreakDuration": "00:05:00"  // Cap at 5 minutes
  }
}
```

**Why this matters**:
- Without a cap, exponential backoff could grow to hours or days
- Ensures regular retry attempts even after many failures
- Balances between not hammering Redis and detecting recovery quickly

**Recommendations**:
- **Development**: `00:01:00` to `00:05:00` (1-5 minutes)
- **Production**: `00:05:00` to `00:10:00` (5-10 minutes)
- **High-availability**: `00:02:00` (2 minutes) for faster recovery detection

---

## Retry Strategies

### Strategy 1: Fast Failover with Infinite Retries (Recommended for Development)

**Goal**: Immediate fallback, keep trying forever, fast recovery detection

```json
{
  "RedisCircuitBreaker": {
    "Enabled": true,
    "ConsecutiveFailuresToOpen": 2,
    "BreakDuration": "00:00:05",
    "HalfOpenProbeTimeout": "00:00:00.250",
    "MaxConsecutiveRetries": 0,
    "UseExponentialBackoff": true,
    "MaxBreakDuration": "00:01:00"
  }
}
```

**Timeline**:
- 0s: Redis fails
- 1s: Circuit opens (retry #1)
- 6s: Test connection (retry #2 if failed)
- 16s: Test connection (retry #3 if failed)
- 36s: Test connection (retry #4 if failed)
- 60s+: Test every 60 seconds (capped)

**Best for**: Local development, testing, staging environments

---

### Strategy 2: Production-Ready with Auto-Recovery

**Goal**: Balance between fast recovery and system stability

```json
{
  "RedisCircuitBreaker": {
    "Enabled": true,
    "ConsecutiveFailuresToOpen": 2,
    "BreakDuration": "00:00:10",
    "HalfOpenProbeTimeout": "00:00:00.500",
    "MaxConsecutiveRetries": 0,
    "UseExponentialBackoff": true,
    "MaxBreakDuration": "00:05:00"
  }
}
```

**Timeline**:
- 0s: Redis fails
- 1s: Circuit opens (retry #1)
- 11s: Test connection (retry #2 if failed)
- 31s: Test connection (retry #3 if failed)
- 71s: Test connection (retry #4 if failed)
- 151s: Test connection (retry #5 if failed)
- 300s+: Test every 5 minutes (capped)

**Best for**: Production applications with long-running processes

---

### Strategy 3: Kubernetes/Auto-Scaling with Bounded Retries

**Goal**: Let orchestrator handle recovery, prevent zombie pods

```json
{
  "RedisCircuitBreaker": {
    "Enabled": true,
    "ConsecutiveFailuresToOpen": 2,
    "BreakDuration": "00:00:30",
    "HalfOpenProbeTimeout": "00:00:01.000",
    "MaxConsecutiveRetries": 10,
    "UseExponentialBackoff": true,
    "MaxBreakDuration": "00:10:00"
  }
}
```

**Timeline**:
- 0s: Redis fails
- 1s: Circuit opens (retry #1)
- 31s: Test connection (retry #2 if failed)
- 91s: Test connection (retry #3 if failed)
- 211s: Test connection (retry #4 if failed)
- ... continues with exponential backoff ...
- ~30 minutes: Gives up after 10 retries (stays open forever)

**Best for**: Kubernetes, Docker Swarm, auto-scaling environments with health checks

---

### Strategy 4: Constant Retries (Testing/Debugging)

**Goal**: Predictable retry intervals for testing

```json
{
  "RedisCircuitBreaker": {
    "Enabled": true,
    "ConsecutiveFailuresToOpen": 2,
    "BreakDuration": "00:00:10",
    "HalfOpenProbeTimeout": "00:00:00.250",
    "MaxConsecutiveRetries": 0,
    "UseExponentialBackoff": false,
    "MaxBreakDuration": "00:05:00"
  }
}
```

**Timeline**:
- Every retry attempts at exactly 10-second intervals
- No backoff growth

**Best for**: Integration tests, debugging circuit breaker behavior

---

### Strategy 5: Aggressive Recovery Detection

**Goal**: Detect Redis recovery as fast as possible

```json
{
  "RedisCircuitBreaker": {
    "Enabled": true,
    "ConsecutiveFailuresToOpen": 2,
    "BreakDuration": "00:00:02",
    "HalfOpenProbeTimeout": "00:00:00.100",
    "MaxConsecutiveRetries": 0,
    "UseExponentialBackoff": true,
    "MaxBreakDuration": "00:00:30"
  }
}
```

**Timeline**:
- 0s: Redis fails
- 1s: Circuit opens (retry #1)
- 3s: Test connection (retry #2 if failed)
- 7s: Test connection (retry #3 if failed)
- 15s: Test connection (retry #4 if failed)
- 30s+: Test every 30 seconds (capped)

**Best for**: Critical applications requiring minimal downtime

---

## Reconciliation

### What is Reconciliation?

When the circuit is open, all cache writes go to **in-memory storage**. When Redis recovers, the **reconciliation service** automatically syncs these in-memory writes back to Redis.

This provides **zero-data-loss failover** for cache operations.

### Configuration

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

### DI Registration

```csharp
builder.Services.AddVapeCacheRedisReconciliation(options =>
{
    options.MaxOperationAge = TimeSpan.FromMinutes(5);
});
```

### Options

#### `Enabled`

**Type**: `bool`
**Default**: `true`
**Description**: Enable/disable reconciliation of in-memory writes back to Redis.

**When to disable**:
- Cache is purely ephemeral (okay to lose data during outages)
- You want faster recovery (no sync overhead)
- Testing scenarios

#### `MaxOperationAge`

**Type**: `TimeSpan`
**Default**: `00:05:00` (5 minutes)
**Description**: Maximum age of tracked operations before they're discarded as stale.

**Purpose**: Prevents syncing very old cache entries that may no longer be relevant.

**Recommendations**:
- **Short-lived cache**: `00:01:00` to `00:05:00` (1-5 minutes)
- **Long-lived cache**: `00:10:00` to `00:30:00` (10-30 minutes)
- **Match your cache TTL**: Set to ~50% of your typical cache entry TTL

### Reconciliation Behavior

When circuit closes (Redis recovers):

1. **Snapshot** all pending in-memory writes
2. **Filter** operations older than `MaxOperationAge`
3. **Sync** remaining operations to Redis:
   - Respect original TTLs (calculate remaining time)
   - Skip operations whose TTL already expired
   - Preserve operation order (last write wins)
4. **Log** reconciliation results:
   ```
   🔄 Syncing 47 in-memory writes back to Redis...
   ✅ Redis reconciliation complete: 45 synced, 2 skipped, 0 failed (took 234ms)
   ```

### Utilities

- `IRedisReconciliationService.FlushAsync()` clears the backing store on demand (useful for admin tooling and test cleanup).

### Important Notes

Reconciliation is **opt-in**. Enable it in DI with `AddVapeCacheRedisReconciliation(...)` so recovery sync is explicit and configurable.

---

## Common Scenarios

### Scenario 1: Redis Server Restart

**Problem**: Redis restarts for maintenance, causing 30-second downtime.

**Circuit Breaker Behavior**:
1. Detects failure after ~1 second (2 connection timeouts)
2. Opens circuit, switches to in-memory mode
3. Application continues working seamlessly
4. Tests Redis every 10s → 20s → 40s → 80s → ...
5. Detects recovery after Redis comes back online
6. Closes circuit, switches back to Redis
7. Syncs any in-memory writes to Redis (if reconciliation enabled)

**User Impact**: ✅ **Zero downtime** - application keeps working

---

### Scenario 2: Network Partition

**Problem**: Network between application and Redis is unstable.

**Circuit Breaker Behavior**:
1. Circuit opens on first failure
2. Uses in-memory cache during partition
3. Exponential backoff reduces network traffic during instability
4. Automatically recovers when network stabilizes

**User Impact**: ✅ **Degraded but functional** - cache works, some staleness acceptable

---

### Scenario 3: Redis Connection Pool Exhaustion

**Problem**: Redis connection pool exhausted, new connections timing out.

**Circuit Breaker Behavior**:
1. Detects connection creation failures
2. Opens circuit to prevent cascading failures
3. In-memory mode reduces load on Redis
4. Allows Redis to recover and drain connections
5. Tests recovery periodically

**User Impact**: ✅ **Automatic load shedding** - prevents total system failure

---

### Scenario 4: Redis Overload (Slow Responses)

**Problem**: Redis is responding slowly due to high load.

**Circuit Breaker Behavior**:
- ⚠️ **Note**: Circuit breaker tracks **connection failures**, not slow responses
- If connections succeed but queries are slow, circuit stays closed
- Consider using **timeout policies** at the command execution level

**Recommendation**: Combine circuit breaker with command-level timeouts and retry policies.

---

### Scenario 5: Permanent Redis Outage

**Problem**: Redis server is decommissioned, never coming back.

**With `MaxConsecutiveRetries: 0` (infinite)**:
- Application keeps retrying forever
- Runs indefinitely in in-memory mode
- Manual intervention required (config change or restart)

**With `MaxConsecutiveRetries: 10` (bounded)**:
- Application retries 10 times (~30 minutes with exponential backoff)
- After 10 retries, circuit stays permanently open
- Logs error indicating max retries exceeded
- Application continues in in-memory mode until restart

**Recommendation**: Use bounded retries in Kubernetes/orchestrated environments.

---

## Monitoring and Observability

### Console Output

The circuit breaker provides clear visual feedback in the console:

```
🔥🔥🔥 ⚡ CIRCUIT BREAKER OPENED! Redis connections failing (retry #1), switching to IN-MEMORY mode for 10 seconds 🔥🔥🔥

🔄 Circuit breaker HALF-OPEN. Testing Redis connection...

✅✅✅ Circuit breaker CLOSED! Redis operations resumed after 3 retries. ✅✅✅

🔄 Syncing 47 in-memory writes back to Redis...
✅ Redis reconciliation complete: 45 synced, 2 skipped, 0 failed (took 234ms)
```

### Structured Logging

All circuit breaker events are logged with structured data:

```csharp
_logger.LogWarning(
    "⚡ CIRCUIT BREAKER OPENED - Redis connections failing (retry #{Retry}). Switching to in-memory mode for {Duration} seconds.",
    consecutiveRetries,
    currentBreakDuration.TotalSeconds);
```

**Log Levels**:
- `Warning`: Circuit opened (Redis failure detected)
- `Information`: Circuit closed (Redis recovered), Half-open state, Reconciliation events
- `Error`: Max retries exceeded, Reconciliation failures

### OpenTelemetry Metrics

Circuit breaker emits OpenTelemetry metrics:

```csharp
CacheTelemetry.RedisBreakerOpened.Add(1, new TagList { { "backend", "hybrid" } });
```

**Available Metrics**:
- `vapecache.redis.breaker.opened`: Count of circuit breaker activations
- `vapecache.cache.backend`: Current backend in use (redis/memory)
- `vapecache.cache.fallback_to_memory`: Count of fallback events

**Monitoring Query Examples** (Prometheus):
```promql
# Circuit breaker activation rate
rate(vapecache_redis_breaker_opened_total[5m])

# Current backend
vapecache_cache_backend{backend="memory"}

# Fallback events
rate(vapecache_cache_fallback_to_memory_total[5m])
```

### Health Checks

You can query circuit breaker state via the `IRedisFailoverController` interface:

```csharp
public interface IRedisFailoverController
{
    bool IsRedisHealthy { get; }
    CircuitState CurrentState { get; }
    int ConsecutiveRetries { get; }
}
```

**Example Health Check**:
```csharp
public class RedisHealthCheck : IHealthCheck
{
    private readonly IRedisFailoverController _failover;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context)
    {
        if (_failover.IsRedisHealthy)
            return HealthCheckResult.Healthy("Redis is healthy");

        return HealthCheckResult.Degraded(
            $"Circuit breaker open (retry #{_failover.ConsecutiveRetries})");
    }
}
```

---

## Advanced Configuration

### Environment-Specific Configuration

Use different settings per environment:

**appsettings.Development.json**:
```json
{
  "RedisCircuitBreaker": {
    "Enabled": true,
    "ConsecutiveFailuresToOpen": 2,
    "BreakDuration": "00:00:05",
    "MaxBreakDuration": "00:01:00",
    "UseExponentialBackoff": true
  }
}
```

**appsettings.Production.json**:
```json
{
  "RedisCircuitBreaker": {
    "Enabled": true,
    "ConsecutiveFailuresToOpen": 2,
    "BreakDuration": "00:00:30",
    "MaxBreakDuration": "00:10:00",
    "UseExponentialBackoff": true,
    "MaxConsecutiveRetries": 10
  }
}
```

### Programmatic Configuration

Override settings in code:

```csharp
services.Configure<RedisCircuitBreakerOptions>(options =>
{
    options.Enabled = true;
    options.ConsecutiveFailuresToOpen = 2;
    options.BreakDuration = TimeSpan.FromSeconds(10);
    options.UseExponentialBackoff = true;
    options.MaxBreakDuration = TimeSpan.FromMinutes(5);
});
```

### Dynamic Configuration

Circuit breaker settings are bound at startup. Apply configuration changes by restarting the process.

---

## Troubleshooting

### Problem: Circuit Breaker Not Triggering

**Symptoms**: Redis is down but circuit stays closed, application hangs.

**Possible Causes**:
1. Circuit breaker is disabled
2. Connection timeout is too long
3. Not enough failures within sampling window

**Solutions**:
```json
// Ensure circuit breaker is enabled
{
  "RedisCircuitBreaker": {
    "Enabled": true
  }
}

// Reduce connection timeout for faster failure detection
{
  "RedisConnection": {
    "ConnectTimeout": "00:00:00.500"  // 500ms
  }
}

// Lower failure threshold
{
  "RedisCircuitBreaker": {
    "ConsecutiveFailuresToOpen": 2
  }
}
```

---

### Problem: Circuit Opens Too Frequently

**Symptoms**: Circuit keeps opening even though Redis is healthy.

**Possible Causes**:
1. Network is intermittently slow
2. Redis is under heavy load
3. Threshold is too aggressive

**Solutions**:
```json
// Increase failure threshold
{
  "RedisCircuitBreaker": {
    "ConsecutiveFailuresToOpen": 5
  }
}

// Increase connection timeout
{
  "RedisConnection": {
    "ConnectTimeout": "00:00:01.000"  // 1 second
  }
}

// Increase probe timeout
{
  "RedisCircuitBreaker": {
    "HalfOpenProbeTimeout": "00:00:01.000"  // 1 second
  }
}
```

---

### Problem: Circuit Never Recovers

**Symptoms**: Circuit opened once and never closed, even after Redis recovered.

**Possible Causes**:
1. Max retries exceeded
2. Probe timeout too short
3. Redis requires authentication/TLS but not configured

**Solutions**:
```json
// Ensure infinite retries
{
  "RedisCircuitBreaker": {
    "MaxConsecutiveRetries": 0
  }
}

// Increase probe timeout
{
  "RedisCircuitBreaker": {
    "HalfOpenProbeTimeout": "00:00:01.000"
  }
}

// Check Redis connection settings
{
  "RedisConnection": {
    "Host": "localhost",
    "Port": 6379,
    "Password": "your-password",  // If required
    "UseTls": false
  }
}
```

Check logs for specific errors:
```
Circuit breaker: Max retries (10) exceeded. Staying in OPEN state indefinitely.
```

---

### Problem: Slow Recovery Detection

**Symptoms**: Redis comes back online but circuit stays open for too long.

**Possible Causes**:
1. Break duration is too long
2. Exponential backoff has grown large
3. Max break duration is too high

**Solutions**:
```json
// Reduce initial break duration
{
  "RedisCircuitBreaker": {
    "BreakDuration": "00:00:05"  // 5 seconds
  }
}

// Reduce max break duration cap
{
  "RedisCircuitBreaker": {
    "MaxBreakDuration": "00:01:00"  // 1 minute
  }
}

// Disable exponential backoff for constant retries
{
  "RedisCircuitBreaker": {
    "UseExponentialBackoff": false
  }
}
```

---

### Problem: Reconciliation Not Working

**Symptoms**: In-memory writes are not synced back to Redis after recovery.

**Cause**: Reconciliation is opt-in. Ensure `AddVapeCacheRedisReconciliation(...)` is registered and your options are valid.

**Workaround**: Circuit breaker still works without reconciliation; writes during outage remain in-memory until Redis recovery.

---

### Problem: Too Much Console Noise

**Symptoms**: Circuit breaker messages flooding console.

**Solutions**:
```json
// Reduce log level for VapeCache
{
  "Serilog": {
    "MinimumLevel": {
      "Override": {
        "VapeCache.Infrastructure": "Warning"  // Only warnings and errors
      }
    }
  }
}
```

Or disable console output (messages still logged):
```csharp
// Circuit breaker code still logs, but Console.WriteLine can be removed
// This would require a code change - consider using log levels instead
```

---

## Summary

### Recommended Production Configuration

```json
{
  "RedisCircuitBreaker": {
    "Enabled": true,
    "ConsecutiveFailuresToOpen": 2,
    "BreakDuration": "00:00:10",
    "HalfOpenProbeTimeout": "00:00:00.500",
    "MaxConsecutiveRetries": 0,
    "UseExponentialBackoff": true,
    "MaxBreakDuration": "00:05:00"
  },
  "RedisReconciliation": {
    "Enabled": true,
    "MaxOperationAge": "00:05:00"
  }
}
```

### Key Takeaways

✅ **Fast Failover**: Circuit opens within ~1 second of Redis failure
✅ **Zero Downtime**: Application continues working in in-memory mode
✅ **Automatic Recovery**: Detects when Redis comes back online
✅ **Exponential Backoff**: Reduces load on struggling Redis instance
✅ **Configurable Retries**: Infinite or bounded retry strategies
✅ **Observable**: Clear console output and structured logging
✅ **Production-Ready**: Battle-tested with Polly circuit breaker library

### Architecture

The circuit breaker is implemented at the **connection factory layer** using [Polly](https://github.com/App-vNext/Polly):

- **File**: [CircuitBreakerRedisConnectionFactory.cs](../VapeCache.Infrastructure/Connections/CircuitBreakerRedisConnectionFactory.cs)
- **Pattern**: Wraps `RedisConnectionFactory` with `ResiliencePipeline<Result<IRedisConnection>>`
- **Library**: Polly 8.6.5 with full Result<T> support
- **State Management**: Tracks circuit state and retry count
- **Reconciliation**: Triggers sync on recovery (when enabled)

---

For questions or issues, see the [GitHub Issues](https://github.com/your-repo/vapecache/issues).
