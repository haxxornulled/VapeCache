# VapeCache Failure Scenarios

Comprehensive guide to failure modes, recovery behavior, and operational runbooks for VapeCache in production.

## Table of Contents

- [Overview](#overview)
- [Network Failures](#network-failures)
- [Redis Server Failures](#redis-server-failures)
- [Application Failures](#application-failures)
- [Resource Exhaustion](#resource-exhaustion)
- [Configuration Errors](#configuration-errors)
- [Circuit Breaker Behavior](#circuit-breaker-behavior)
- [Failure Matrix](#failure-matrix)
- [Troubleshooting](#troubleshooting)

---

## Overview

VapeCache is designed for **graceful degradation** under failure. This document describes:

1. **What fails** (network, Redis, application, resources)
2. **How VapeCache responds** (circuit breaker, fallback, auto-reconnect)
3. **What users experience** (degraded performance, cache misses, errors)
4. **How to recover** (manual intervention, automatic recovery, monitoring)

**Design Principle:** Cache failures should **never take down the application**. VapeCache falls back to in-memory cache or returns cache misses gracefully.

---

## Network Failures

### Scenario 1: Redis Connection Timeout

**What Happens:**
- TCP connection to Redis times out (firewall drop, network partition)
- Connection attempt fails after `ConnectTimeoutMs` (default: 5000ms)

**VapeCache Behavior:**
```
1. Connection pool attempts to connect
2. Wait up to ConnectTimeoutMs (5 seconds)
3. If timeout → Throw RedisConnectionException
4. Circuit breaker records failure
5. After 5 failures in 30 seconds → Circuit opens
6. All subsequent requests → Use in-memory cache
```

**User Experience:**
- **Before circuit opens:** Slow cache operations (5 second timeout per attempt)
- **After circuit opens:** Fast cache operations (in-memory fallback)
- **Cache miss rate:** Increases (in-memory cache is empty)

**Recovery:**
- **Automatic:** Circuit breaker probes Redis every 60 seconds (half-open state)
- **Manual:** Fix network connectivity, restart application (if needed)

**Metrics to Monitor:**
- `redis.connect.failures` (Counter) - Connection failures
- `redis.pool.timeouts` (Counter) - Pool acquisition timeouts
- Circuit breaker state logs: `Circuit opened due to failures`

**Runbook:**
```bash
# 1. Check network connectivity
ping redis-host

# 2. Check firewall rules
telnet redis-host 6379

# 3. Check Redis is running
redis-cli -h redis-host PING

# 4. Monitor circuit breaker
# Watch logs for "Circuit opened" / "Circuit closed" events
```

---

### Scenario 2: Network Partition (Mid-Connection)

**What Happens:**
- TCP connection is established, then network drops
- Socket operations fail with `IOException` or `SocketException`

**VapeCache Behavior:**
```
1. Command execution fails with IOException
2. Connection is marked as faulted
3. Pending commands are drained → Complete with OperationCanceledException
4. Connection released back to pool (will be reaped)
5. Circuit breaker records failure
6. Next command → Acquire new connection (auto-reconnect)
```

**User Experience:**
- **In-flight commands:** Fail with `OperationCanceledException`
- **New commands:** Retry on new connection (automatic)
- **Circuit opens:** After 5 failures, fallback to in-memory

**Recovery:**
- **Automatic:** Auto-reconnect on next command
- **Manual:** None required (transparent reconnect)

**Metrics to Monitor:**
- `redis.cmd.failures` (Counter) - Command failures
- `redis.pool.drops` (Counter) - Dropped connections

**Runbook:**
```bash
# 1. Check for network partition
# Look for socket errors in logs

# 2. Monitor auto-reconnect
# Watch for "Connection faulted, reconnecting" logs

# 3. Verify circuit breaker state
# Should NOT open for transient network blips
```

---

### Scenario 3: TLS Certificate Validation Failure

**What Happens:**
- TLS handshake fails due to invalid certificate (expired, self-signed, hostname mismatch)

**VapeCache Behavior:**
```
1. SslStream.AuthenticateAsClientAsync() throws AuthenticationException
2. Connection factory records failure
3. Throws RedisConnectionException with inner exception
4. Circuit breaker records failure
5. After 5 failures → Circuit opens
```

**User Experience:**
- **All cache operations fail** (cannot establish TLS connection)
- **Circuit opens** → Fallback to in-memory cache

**Recovery:**
- **Manual:** Fix certificate (renew, add to trust store, fix hostname)

**Metrics to Monitor:**
- `redis.connect.failures` (Counter) - Connection failures
- Logs: `AuthenticationException: The remote certificate is invalid`

**Runbook:**
```bash
# 1. Check certificate expiration
openssl s_client -connect redis-host:6380 -servername redis-host < /dev/null 2>&1 | openssl x509 -noout -dates

# 2. Verify certificate CN/SAN matches TlsHost
openssl s_client -connect redis-host:6380 -servername redis-host < /dev/null 2>&1 | openssl x509 -noout -text | grep DNS

# 3. Test TLS connection manually
openssl s_client -connect redis-host:6380 -servername redis-host
```

See [TLS_SECURITY.md](TLS_SECURITY.md) for certificate setup.

---

## Redis Server Failures

### Scenario 4: Redis Server Crash (Immediate)

**What Happens:**
- Redis process crashes (OOM kill, segfault, manual shutdown)
- All socket connections receive `ConnectionResetException`

**VapeCache Behavior:**
```
1. All in-flight commands fail with IOException
2. All connections marked as faulted
3. Connections drained and released
4. Circuit breaker records 5+ failures instantly
5. Circuit opens → Fallback to in-memory cache
```

**User Experience:**
- **Immediate:** Spike in `OperationCanceledException` for in-flight commands
- **Within 1 second:** Circuit opens, all requests use in-memory
- **Cache hit rate:** Drops to 0% (in-memory cache is empty)
- **Application continues:** No downtime, gracefully degraded

**Recovery:**
- **Automatic:** Circuit probes Redis every 60 seconds (half-open)
- **Manual:** Restart Redis server

**Metrics to Monitor:**
- `redis.cmd.failures` (Counter) - Sudden spike
- `redis.pool.drops` (Counter) - All connections dropped
- Circuit breaker logs: `Circuit opened due to failures`

**Runbook:**
```bash
# 1. Check if Redis is running
systemctl status redis

# 2. Restart Redis
systemctl start redis

# 3. Monitor circuit breaker recovery
# Watch logs for "Circuit transitioned to half-open"
# Watch logs for "Circuit closed" (recovery successful)
```

---

### Scenario 5: Redis Server Slowdown (Gradual Degradation)

**What Happens:**
- Redis becomes slow (high CPU, memory swapping, slow disk I/O)
- Commands take longer than `CommandTimeoutMs` (default: 5000ms)

**VapeCache Behavior:**
```
1. Commands timeout after CommandTimeoutMs
2. Timeout exceptions recorded by circuit breaker
3. After 5 timeouts in 30 seconds → Circuit opens
4. Fallback to in-memory cache
```

**User Experience:**
- **Before circuit opens:** Slow cache operations (5 second timeout per command)
- **After circuit opens:** Fast cache operations (in-memory fallback)
- **Cache hit rate:** Decreases (in-memory cache warms up slowly)

**Recovery:**
- **Automatic:** Circuit probes Redis every 60 seconds
- **Manual:** Investigate Redis slowdown (use `redis-cli --latency`, `INFO stats`)

**Metrics to Monitor:**
- `redis.cmd.ms` (Histogram) - Command latency increases
- `redis.cmd.failures` (Counter) - Timeout failures
- Redis metrics: `instantaneous_ops_per_sec`, `used_memory`, `total_commands_processed`

**Runbook:**
```bash
# 1. Check Redis latency
redis-cli --latency

# 2. Check Redis stats
redis-cli INFO stats

# 3. Check for slow commands
redis-cli SLOWLOG GET 10

# 4. Check memory usage
redis-cli INFO memory

# 5. Restart Redis if unresponsive
systemctl restart redis
```

---

### Scenario 6: Redis Server Restart (Planned Maintenance)

**What Happens:**
- Redis is restarted gracefully (e.g., upgrade, config change)
- Existing connections receive `ConnectionResetException`
- New connections succeed immediately

**VapeCache Behavior:**
```
1. Existing connections fail → Marked as faulted
2. Pending commands drained → Complete with OperationCanceledException
3. Next commands → Auto-reconnect on new connection
4. Circuit breaker: Likely does NOT open (failures < 5 in 30 seconds)
```

**User Experience:**
- **In-flight commands:** Fail with `OperationCanceledException` (< 100ms disruption)
- **New commands:** Succeed after auto-reconnect
- **No circuit breaker activation** (transient failure)

**Recovery:**
- **Automatic:** Auto-reconnect on next command

**Metrics to Monitor:**
- `redis.pool.drops` (Counter) - Connections dropped
- `redis.connect.attempts` (Counter) - Reconnect attempts
- Logs: `Connection faulted, reconnecting`

**Runbook:**
```bash
# 1. Before restart, prepare for connection drops
# (VapeCache handles this automatically)

# 2. Restart Redis
systemctl restart redis

# 3. Monitor auto-reconnect
# Watch logs for "Connection established" events
```

---

## Application Failures

### Scenario 7: Application Startup (Redis Unavailable)

**What Happens:**
- Application starts, but Redis is not yet available (deployment race condition)
- Connection pool warm-up fails

**VapeCache Behavior:**
```
1. Connection pool attempts to create MinPoolSize connections
2. If WarmPoolOnStartup=true → Throw exception, application fails to start
3. If WarmPoolOnStartup=false → Defer connection to first command
```

**User Experience:**
- **WarmPoolOnStartup=true:** Application startup fails (fail-fast)
- **WarmPoolOnStartup=false:** Application starts, first commands fail until Redis available

**Recovery:**
- **Manual:** Ensure Redis is available before application starts
- **Automatic:** Startup preflight retries (if implemented)

**Configuration:**
```json
{
  "RedisConnectionPool": {
    "WarmPoolOnStartup": false  // Defer connection to runtime
  }
}
```

**Runbook:**
```bash
# 1. Ensure Redis is running before application deployment
systemctl start redis

# 2. Use health checks to verify Redis before routing traffic
# (e.g., Kubernetes readiness probe)

# 3. Disable WarmPoolOnStartup for resilience
```

---

### Scenario 8: High Concurrency (Pool Exhaustion)

**What Happens:**
- Application receives spike in traffic
- Connection pool exhausted (all connections in use)
- `AcquireTimeoutMs` exceeded

**VapeCache Behavior:**
```
1. Caller waits up to AcquireTimeoutMs for available connection
2. If timeout → Throw TimeoutException
3. Circuit breaker: Does NOT open (not a Redis failure)
```

**User Experience:**
- **Cache operations fail** with `TimeoutException`
- **Application continues** (cache miss, fetch from database)

**Recovery:**
- **Manual:** Increase `MaxPoolSize` or `AcquireTimeoutMs`
- **Automatic:** Traffic decreases, pool pressure relieved

**Metrics to Monitor:**
- `redis.pool.timeouts` (Counter) - Pool acquisition timeouts
- `redis.pool.wait.ms` (Histogram) - Wait time increases

**Configuration:**
```json
{
  "RedisConnectionPool": {
    "MaxPoolSize": 20,           // Increase pool size
    "AcquireTimeoutMs": 10000    // Increase wait time
  }
}
```

**Runbook:**
```bash
# 1. Monitor pool exhaustion
# Watch redis.pool.timeouts metric

# 2. Increase MaxPoolSize if sustained high traffic
# Edit appsettings.json or set environment variable

# 3. Investigate slow commands (may be blocking pool)
redis-cli SLOWLOG GET 10
```

---

## Resource Exhaustion

### Scenario 9: Memory Exhaustion (In-Memory Cache)

**What Happens:**
- Circuit breaker is open (fallback to in-memory cache)
- In-memory cache grows beyond `InMemoryCacheSizeLimitMb`

**VapeCache Behavior:**
```
1. IMemoryCache evicts oldest entries (LRU)
2. Cache hit rate decreases
3. Application continues (cache misses fetch from database)
```

**User Experience:**
- **Cache hit rate:** Decreases as evictions increase
- **Database load:** Increases (more cache misses)

**Recovery:**
- **Manual:** Increase `InMemoryCacheSizeLimitMb` (if memory available)
- **Automatic:** Circuit closes, Redis becomes available

**Configuration:**
```json
{
  "CacheService": {
    "InMemoryCacheSizeLimitMb": 500  // Increase limit
  }
}
```

**Runbook:**
```bash
# 1. Monitor in-memory cache size
# (Microsoft.Extensions.Caching.Memory metrics)

# 2. Restore Redis to reduce in-memory pressure
systemctl start redis

# 3. Increase in-memory limit if Redis outage prolonged
```

---

### Scenario 10: Socket Exhaustion (Too Many Connections)

**What Happens:**
- Application creates too many connections to Redis
- Operating system socket limit exceeded

**VapeCache Behavior:**
```
1. Connection creation fails with SocketException
2. Circuit breaker records failure
3. After 5 failures → Circuit opens
```

**User Experience:**
- **Connection failures** until circuit opens
- **Fallback to in-memory cache**

**Recovery:**
- **Manual:** Reduce `MaxPoolSize` or increase OS socket limit

**Configuration:**
```json
{
  "RedisConnectionPool": {
    "MaxPoolSize": 10  // Reduce connection count
  }
}
```

**Runbook:**
```bash
# 1. Check socket limit
ulimit -n

# 2. Increase socket limit (Linux)
# Edit /etc/security/limits.conf
# Add: * soft nofile 65536

# 3. Reduce MaxPoolSize if hitting OS limits
```

---

## Configuration Errors

### Scenario 11: Invalid Connection String

**What Happens:**
- Connection string is malformed (wrong host, port, etc.)

**VapeCache Behavior:**
```
1. Connection factory attempts to parse connection string
2. If invalid → Throw ArgumentException at startup
```

**User Experience:**
- **Application fails to start** (fail-fast)

**Recovery:**
- **Manual:** Fix connection string in appsettings.json or environment variable

**Runbook:**
```bash
# 1. Validate connection string format
# redis://[username:password@]host:port[/database][?useTls=true]

# 2. Test connection manually
redis-cli -h host -p port PING
```

---

### Scenario 12: AllowInvalidCert in Production

**What Happens:**
- `AllowInvalidCert=true` in production environment

**VapeCache Behavior:**
```
1. Connection factory detects production environment
2. Throws InvalidOperationException at startup
3. Application fails to start
```

**User Experience:**
- **Application fails to start** (security safeguard)

**Recovery:**
- **Manual:** Use proper CA-signed certificates, set `AllowInvalidCert=false`

**Runbook:**
```bash
# 1. Verify environment
echo $ASPNETCORE_ENVIRONMENT

# 2. Use proper certificates in production
# See TLS_SECURITY.md for Let's Encrypt setup
```

---

## Circuit Breaker Behavior

### State Transitions

```
┌──────────────┐
│ Closed       │ (Redis healthy)
│ Use Redis    │
└──────────────┘
        │
        │ 5 failures in 30 seconds
        ↓
┌──────────────┐
│ Open         │ (Redis down)
│ Use Memory   │
└──────────────┘
        │
        │ Wait 60 seconds
        ↓
┌──────────────┐
│ Half-Open    │ (Recovery probe)
│ Try Redis    │
└──────────────┘
        │
        ├─→ Success → Closed
        └─→ Failure → Open (wait another 60 seconds)
```

### Configuration

```json
{
  "CacheService": {
    "CircuitBreakerFailureThreshold": 5,        // Failures before opening
    "CircuitBreakerSamplingDurationSeconds": 30, // Time window for counting
    "CircuitBreakerBreakDurationSeconds": 60     // How long to wait before half-open
  }
}
```

### Tuning Guidance

**Aggressive (Fail Fast):**
```json
{
  "CircuitBreakerFailureThreshold": 3,
  "CircuitBreakerSamplingDurationSeconds": 10,
  "CircuitBreakerBreakDurationSeconds": 30
}
```

**Conservative (Tolerate Transients):**
```json
{
  "CircuitBreakerFailureThreshold": 10,
  "CircuitBreakerSamplingDurationSeconds": 60,
  "CircuitBreakerBreakDurationSeconds": 120
}
```

---

## Failure Matrix

| Failure Type | Circuit Opens? | Auto-Recover? | User Impact | Mitigation |
|--------------|----------------|---------------|-------------|------------|
| Connection timeout | Yes (5x) | Yes (probe) | Slow → Fast fallback | Fix network |
| Network partition | Yes (5x) | Yes (auto-reconnect) | In-flight fail, new succeed | None (automatic) |
| TLS cert invalid | Yes (5x) | No | All fail → Fallback | Fix certificate |
| Redis crash | Yes (instant) | Yes (probe) | Transient fail → Fallback | Restart Redis |
| Redis slowdown | Yes (5x timeouts) | Yes (probe) | Slow → Fast fallback | Fix Redis perf |
| Redis restart | No (< 5 failures) | Yes (auto-reconnect) | < 100ms disruption | None (automatic) |
| App startup, no Redis | No (manual) | N/A | Startup fails or deferred | Ensure Redis up |
| Pool exhaustion | No (not Redis failure) | Auto (traffic decreases) | Timeout exceptions | Increase MaxPoolSize |
| Memory exhaustion | No (app-level) | Auto (eviction) | Cache miss rate increases | Increase limit or fix Redis |
| Socket exhaustion | Yes (5x) | Manual | Connection fail → Fallback | Reduce MaxPoolSize or increase limit |
| Invalid config | No (startup fail) | Manual | App won't start | Fix configuration |

---

## Troubleshooting

### Quick Diagnostics

**Check Redis Connectivity:**
```bash
redis-cli -h <host> -p <port> PING
```

**Check Circuit Breaker State:**
```bash
# Watch application logs for:
# "Circuit opened due to failures"
# "Circuit transitioned to half-open"
# "Circuit closed"
```

**Check Metrics:**
```bash
# Prometheus query (if using Prometheus exporter):
redis_connect_failures_total
redis_cmd_failures_total
redis_pool_timeouts_total
```

**Check Redis Health:**
```bash
redis-cli INFO stats
redis-cli --latency
redis-cli SLOWLOG GET 10
```

### Common Issues

**Issue:** High `redis.pool.timeouts`
**Cause:** Pool exhaustion (MaxPoolSize too small)
**Fix:** Increase `MaxPoolSize` or investigate slow commands

**Issue:** High `redis.cmd.failures`
**Cause:** Redis unavailable, network issues, or Redis slowdown
**Fix:** Check Redis server health, network connectivity

**Issue:** Circuit breaker opens frequently
**Cause:** Redis unreliable or thresholds too aggressive
**Fix:** Increase `CircuitBreakerFailureThreshold` or fix Redis reliability

**Issue:** TLS certificate validation fails
**Cause:** Expired certificate, hostname mismatch, or untrusted CA
**Fix:** See [TLS_SECURITY.md](TLS_SECURITY.md) for certificate setup

---

## See Also

- [CONFIGURATION.md](CONFIGURATION.md) - Configuration reference
- [ARCHITECTURE.md](ARCHITECTURE.md) - Circuit breaker implementation
- [TLS_SECURITY.md](TLS_SECURITY.md) - TLS certificate setup
- [OBSERVABILITY_ARCHITECTURE.md](OBSERVABILITY_ARCHITECTURE.md) - Metrics and logging
