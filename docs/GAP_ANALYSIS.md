# VapeCache Gap Analysis & Roadmap

## Executive Summary

VapeCache has **strong fundamentals** in transport, performance, reliability, and observability. This document identifies gaps that must be addressed before wider adoption, prioritized by impact and risk.

## What's Strong (Keep Doing This) ✅

### Transport / Performance
- ✅ **Ordered multiplexing**: `Channel<>` + pooled `IValueTaskSource` beats StackExchange.Redis's `TaskCompletionSource` churn
- ✅ **Deterministic buffer ownership**: `ArrayPool` for bulk replies avoids LOH spikes
- ✅ **Fault handling**: Drain-on-fault → release slots → drop bad sockets → reconnect on next op
- ✅ **Pool hygiene**: Drop reasons, reaper, validate-on-borrow is production-ready

### Reliability Patterns
- ✅ **Hybrid cache**: Circuit breaker + stampede protection (correct layering)
- ✅ **Startup preflight**: Avoids "Redis flaps = app dies" failure mode
- ✅ **Clean separation**: Pool mode vs mux mode for stress testing

### Observability
- ✅ **OpenTelemetry**: Meters + tracing at connect/pool/command levels
- ✅ **Serilog correlation**: Span enrichment for trace correlation

### Ergonomics
- ✅ **Console host**: CLI verification/logging (no HTTP endpoints)
- ✅ **Secret handling**: Environment variable indirection (CI-friendly)

## Critical Gaps (Must Fix Before v1.0)

### 1. RESP Protocol Surface Area 🔴 **CRITICAL**

**Problem:** Unclear what RESP features are supported/unsupported.

**Impact:** Users may assume features work that don't (Lua, Pub/Sub, Streams).

**Action Items:**
- [ ] Document RESP2 vs RESP3 support explicitly
- [ ] State feature gaps:
  - ❌ Lua scripting (EVAL, EVALSHA)
  - ❌ Pub/Sub (SUBSCRIBE, PSUBSCRIBE, PUBLISH)
  - ❌ Streams (XADD, XREAD, XGROUP)
  - ❌ RESP3 push messages
  - ❌ Client-side caching (RESP3)
  - ❌ Cluster mode (MOVED, ASK redirects)
- [ ] Add to [docs/REDIS_PROTOCOL_SUPPORT.md](docs/REDIS_PROTOCOL_SUPPORT.md)

**Deliverable:**
```markdown
# Redis Protocol Support

## Supported (RESP2 Baseline)
✅ String commands (GET, SET, MGET, MSET, GETEX)
✅ Hash commands (HGET, HSET, HMGET)
✅ List commands (LPUSH, RPUSH, LPOP, RPOP, LRANGE, LLEN)
✅ Set commands (SADD, SREM, SMEMBERS, SISMEMBER, SCARD)
✅ Sorted Set commands (ZADD, ZREM, ZRANGE, ZRANGEBYSCORE, ZSCORE, ZRANK, ZCARD, ZINCRBY)
✅ Key commands (DEL, UNLINK, TTL, PTTL)
✅ Connection commands (PING)
✅ Server commands (MODULE LIST)

## Not Supported (Non-Goals)
❌ Lua scripting (EVAL, EVALSHA)
❌ Pub/Sub (SUBSCRIBE, PUBLISH)
❌ Streams (XADD, XREAD)
❌ Transactions (MULTI, EXEC)
❌ RESP3 protocol
❌ Cluster mode (MOVED, ASK)
❌ Client-side caching
```

---

### 2. Backpressure Semantics 🟡 **HIGH PRIORITY**

**Problem:** Behavior undefined when `MaxInFlightPerConnection` is hit.

**Impact:** Users don't know if operations queue, fail-fast, or block.

**Current Behavior (Needs Documentation):**
```csharp
// RedisMultiplexedConnection.cs - what happens here?
await _writeSem.WaitAsync(ct);  // ← Blocks until slot available
```

**Action Items:**
- [ ] Document blocking behavior with timeout
- [ ] Expose metrics:
  - `redis.mux.queue_depth` (Gauge) - Current pending operations
  - `redis.mux.wait.ms` (Histogram) - Time waiting for slot
  - `redis.mux.throttled` (Counter) - Operations delayed by backpressure
- [ ] Add to [docs/BACKPRESSURE.md](docs/BACKPRESSURE.md)

**Deliverable:**
```markdown
# Backpressure Semantics

## MaxInFlightPerConnection Behavior

When in-flight limit is reached:
1. **Blocks with cancellation support** - `WaitAsync(ct)` until slot available
2. **Respects caller's CancellationToken** - Returns `OperationCanceledException` if canceled
3. **No unbounded queueing** - Max queue depth = `MaxInFlightPerConnection`
4. **Per-connection limit** - Independent backpressure per multiplexed connection

## Metrics
- `redis.mux.queue_depth`: Current pending operations per connection
- `redis.mux.wait.ms`: Time spent waiting for available slot
- `redis.mux.throttled`: Total operations delayed by backpressure

## Tuning Guidance
- **Default: 4096** - Suitable for most workloads
- **High-throughput**: Increase to 8192-16384
- **Low-latency**: Decrease to 1024-2048 (fail-fast)
```

---

### 3. Cancellation Guarantees 🟡 **HIGH PRIORITY**

**Problem:** Undefined behavior when caller cancels at different stages.

**Impact:** Potential buffer leaks, socket corruption, or silent failures.

**Scenarios to Define:**
1. Cancel **before enqueue** → Operation never sent
2. Cancel **after enqueue, before write** → ???
3. Cancel **after write, before read** → ???
4. Cancel **during read** → ???

**Action Items:**
- [ ] Audit cancellation paths in:
  - `RedisMultiplexedConnection.cs` (write loop, read loop)
  - `RedisCommandExecutor.cs` (command methods)
  - `PendingOperation.cs` (IValueTaskSource cancellation)
- [ ] Ensure pooled buffers are always returned
- [ ] Document guarantees in [docs/CANCELLATION.md](docs/CANCELLATION.md)

**Deliverable:**
```markdown
# Cancellation Guarantees

## Cancellation Stages

| Stage | Behavior | Buffer Cleanup | Socket State |
|-------|----------|----------------|--------------|
| Before enqueue | Operation not started | N/A | Unaffected |
| After enqueue, before write | TCS canceled, slot released | Buffers returned | Unaffected |
| After write, before read | Response drained, TCS canceled | Buffers returned | Unaffected |
| During read | Partial read drained, TCS canceled | Buffers returned | Connection marked faulted |

## Implementation Details
- All `PendingOperation` instances track rented buffers
- Cancellation always calls `ReturnBuffers()` before completing TCS
- Read loop drains responses for canceled operations (prevents protocol desync)
- Socket faulted if drain fails (connection dropped, reaper cleans up)
```

---

### 4. Thread-Affinity & SyncContext 🟢 **MEDIUM PRIORITY**

**Problem:** Unclear if library is sync-context agnostic.

**Impact:** Deadlocks in UI apps or ASP.NET Classic.

**Action Items:**
- [ ] Verify all `async`/`await` uses `.ConfigureAwait(false)`
- [ ] Audit for blocking calls (`Task.Result`, `Task.Wait()`)
- [ ] Document guarantees in [docs/THREADING.md](docs/THREADING.md)

**Deliverable:**
```markdown
# Threading Model

## Synchronization Context
✅ **Fully sync-context agnostic** - Safe to use in WinForms, WPF, ASP.NET Classic

All async operations use `.ConfigureAwait(false)` to avoid capturing SyncContext.

## Thread Safety
✅ **All public APIs are thread-safe** - Concurrent calls from multiple threads supported
✅ **No thread-affinity** - Operations can complete on any thread
✅ **No blocking I/O** - All I/O is async (no `Task.Result` or `Task.Wait()`)

## Continuations
⚠️ **Continuations MAY inline on I/O threads** in some cases:
- When operation completes synchronously (cache hit, error before I/O)
- Use `.ConfigureAwait(false)` in caller code if continuation is expensive
```

---

### 5. Memory Accounting 🟢 **MEDIUM PRIORITY**

**Problem:** No visibility into pooled buffer usage.

**Impact:** Can't detect pathological workloads causing excessive allocations.

**Action Items:**
- [ ] Add metrics:
  - `redis.pool.buffers_rented` (Counter)
  - `redis.pool.buffers_returned` (Counter)
  - `redis.pool.buffers_outstanding` (Gauge) - Currently rented
  - `redis.pool.buffers_hwm` (Gauge) - High-water mark
- [ ] Track in `RedisValueLease` and `PendingOperation`
- [ ] Expose via OpenTelemetry meters

**Deliverable:**
```csharp
public static class RedisTelemetry
{
    // Existing metrics...

    // Memory accounting (NEW)
    public static readonly Counter<long> BuffersRented = Meter.CreateCounter<long>("redis.pool.buffers_rented");
    public static readonly Counter<long> BuffersReturned = Meter.CreateCounter<long>("redis.pool.buffers_returned");
    public static readonly ObservableGauge<long> BuffersOutstanding = Meter.CreateObservableGauge<long>("redis.pool.buffers_outstanding", () => _buffersOutstanding);
    public static readonly ObservableGauge<long> BuffersHwm = Meter.CreateObservableGauge<long>("redis.pool.buffers_hwm", () => _buffersHwm);

    private static long _buffersOutstanding;
    private static long _buffersHwm;

    internal static void TrackRent()
    {
        BuffersRented.Add(1);
        var current = Interlocked.Increment(ref _buffersOutstanding);
        UpdateHwm(current);
    }

    internal static void TrackReturn()
    {
        BuffersReturned.Add(1);
        Interlocked.Decrement(ref _buffersOutstanding);
    }

    private static void UpdateHwm(long current)
    {
        var hwm = Volatile.Read(ref _buffersHwm);
        while (current > hwm)
        {
            if (Interlocked.CompareExchange(ref _buffersHwm, current, hwm) == hwm)
                break;
            hwm = Volatile.Read(ref _buffersHwm);
        }
    }
}
```

---

### 6. Circuit Breaker Probe Starvation 🟡 **HIGH PRIORITY**

**Problem:** Half-open probe with multiplexing may starve normal traffic.

**Impact:** All traffic blocked waiting for probe, or probe starved by traffic.

**Current Implementation (Needs Review):**
```csharp
// HybridCacheService.cs:115-122
if (_breaker.Enabled && Volatile.Read(ref _openUntilTicks) != 0)
{
    if (Interlocked.CompareExchange(ref _halfOpenProbeInFlight, 1, 0) != 0)
    {
        // Probe already in flight - fallback to memory
        return await memory.GetAsync(key, ct);
    }
    probeTaken = true;
}
```

**Concern:** If probe takes 5 seconds (timeout), all traffic waits or falls back.

**Options:**
1. **Current approach** (one probe, fallback rest) - Simple, may lose cache hits during probe
2. **Dedicated probe connection** - Isolates probe from normal traffic
3. **Probe timeout** (current: `HalfOpenProbeTimeout`) - Good, verify it works

**Action Items:**
- [ ] Verify probe timeout is enforced correctly
- [ ] Add metric: `redis.breaker.probe_duration.ms`
- [ ] Consider dedicated probe connection for high-throughput scenarios
- [ ] Document behavior in [docs/CIRCUIT_BREAKER.md](docs/CIRCUIT_BREAKER.md)

---

### 7. TLS Security Documentation 🟡 **HIGH PRIORITY**

**Problem:** TLS defaults not clearly documented.

**Impact:** Users may misconfigure production TLS.

**Action Items:**
- [ ] Document TLS defaults:
  - Cipher policy (OS default)
  - SNI behavior (uses `TlsHost` or `Host`)
  - Certificate validation (strict by default)
- [ ] Add warning banner for `AllowInvalidCert`
- [ ] Add to [docs/TLS_SECURITY.md](docs/TLS_SECURITY.md)

**Deliverable:**
```markdown
# TLS Security Best Practices

## Default Configuration (Production-Safe)
✅ **TLS 1.2+ only** - `SslProtocols.Tls12 | SslProtocols.Tls13`
✅ **Strict certificate validation** - `AllowInvalidCert=false` (default)
✅ **OS cipher policy** - Uses system-configured cipher suites
✅ **SNI enabled** - Uses `TlsHost` (or `Host` if not specified)

## ⚠️ Development-Only Settings

```json
{
  "RedisConnection": {
    "AllowInvalidCert": true  // ⚠️ DANGER: Disables cert validation
  }
}
```

**CRITICAL:** VapeCache **blocks `AllowInvalidCert=true` in production** automatically.

```csharp
// RedisConnectionFactory.cs:73-79
if (effective.AllowInvalidCert && IsProductionEnvironment())
{
    throw new InvalidOperationException(
        "AllowInvalidCert=true is not permitted in production environments. " +
        "This setting bypasses TLS certificate validation and creates a critical security vulnerability.");
}
```

## Production Checklist
- [ ] `UseTls=true`
- [ ] `AllowInvalidCert=false` (or omit - defaults to false)
- [ ] Valid CA-signed certificate on Redis server
- [ ] `TlsHost` matches certificate CN/SAN
- [ ] Test connection before deployment: `dotnet run --project VapeCache.Console`
```

---

## Documentation Gaps (High ROI)

### 8. One-Page Mental Model 🟡 **HIGH PRIORITY**

**Missing:** Clear explanation of pool vs mux, ordering guarantees.

**Deliverable:** [docs/MENTAL_MODEL.md](docs/MENTAL_MODEL.md)

**Contents:**
- Pool mode: One connection per operation (no ordering)
- Mux mode: Ordered pipelining per connection (FIFO guarantee)
- When to use each mode
- Diagram showing request flow

---

### 9. Failure Matrix 🟡 **HIGH PRIORITY**

**Missing:** What happens when Redis fails at different stages.

**Deliverable:** [docs/FAILURE_SCENARIOS.md](docs/FAILURE_SCENARIOS.md)

**Contents:**

| Scenario | Detection | Recovery | User Impact |
|----------|-----------|----------|-------------|
| Redis down at startup | Preflight fails | Failover to memory (if `FailoverToMemoryOnFailure=true`) | Degraded (memory-only cache) |
| Redis dies mid-flight | Socket exception on write/read | Drain pending, mark faulted, reconnect on next op | Failed operations return error, next op reconnects |
| Network partition | Timeout on connect/read | Circuit breaker opens, fallback to memory | Degraded (memory-only cache) |
| Auth failure | AUTH command fails | Startup fails (if `FailFast=true`) | App won't start |
| Slow Redis (p99 spike) | Command timeout | No automatic action | Timeout exceptions to caller |

---

### 10. Benchmark Methodology 🟢 **MEDIUM PRIORITY**

**Missing:** How to reproduce results exactly.

**Deliverable:** [docs/BENCHMARKING.md](docs/BENCHMARKING.md)

**Contents:**
- Exact payload sizes (32B, 256B, 1KB, 4KB)
- Concurrency levels (1, 8, 64, 256)
- RTT to Redis (localhost, 1ms, 10ms, 50ms)
- CPU pinning (`Process.ProcessorAffinity`)
- OS tuning (TCP window, keepalive)
- How to run SER twin for comparison

---

### 11. Steady-State vs Recovery Benchmarks 🟡 **HIGH PRIORITY**

**Missing:** Performance during fault recovery.

**Why it matters:** SER often looks fine until reconnect storms.

**Deliverable:** Add to [VapeCache.Benchmarks](VapeCache.Benchmarks/)

**Scenarios:**
- Steady-state GET (baseline)
- Burst enqueue (measure queue depth)
- Socket kill mid-operation (measure drain time)
- Reconnect after fault (time to first successful op)

**Example Results Table:**
| Scenario | Metric | VapeCache | StackExchange.Redis |
|----------|--------|-----------|---------------------|
| Steady GET | ops/sec, p99 | 250k, 0.8ms | 220k, 1.2ms |
| Burst enqueue | max queue depth | 4096 (capped) | Unbounded |
| Socket kill | drain time | 12ms | 45ms |
| Reconnect | time to first op | 18ms | 150ms |

---

## Strategic Positioning

### 12. Public Non-Goals List 🟡 **HIGH PRIORITY**

**Purpose:** Set clear expectations before v1.0.

**Deliverable:** [docs/NON_GOALS.md](docs/NON_GOALS.md)

**Contents:**
```markdown
# VapeCache Non-Goals

## What VapeCache Is
✅ **Enterprise Redis transport** - Production-grade, observable, predictable memory
✅ **Hybrid cache** - Redis + in-memory fallback with circuit breaker
✅ **High-performance baseline** - Ordered multiplexing, zero-copy leasing

## What VapeCache Is NOT
❌ **Full Redis client** - Not a drop-in replacement for StackExchange.Redis
❌ **Cluster support** - Single-instance or sentinel only
❌ **Lua scripting** - No EVAL/EVALSHA (planned future work)
❌ **Pub/Sub** - No SUBSCRIBE/PUBLISH (non-goal)
❌ **Streams** - No XADD/XREAD (future consideration)
❌ **RESP3** - RESP2 only (future consideration)

## API Freeze Commitment (v1.0)
Once published to NuGet, `VapeCache.Abstractions` APIs will follow semantic versioning:
- MAJOR: Breaking changes (avoid)
- MINOR: New features (backward-compatible)
- PATCH: Bug fixes only

Current API surface (20 commands) is intentionally small to allow expansion without breaking changes.
```

---

## Roadmap Summary

### Phase 1: Critical Gaps (Before v1.0 NuGet)
**Timeline:** 2 weeks

- [ ] RESP protocol support documentation
- [ ] Backpressure semantics + metrics
- [ ] Cancellation guarantees audit + docs
- [ ] Circuit breaker probe review
- [ ] TLS security documentation
- [ ] Failure matrix documentation
- [ ] Non-goals list

### Phase 2: High-ROI Documentation
**Timeline:** 1 week

- [ ] One-page mental model
- [ ] Threading model documentation
- [ ] Benchmark methodology
- [ ] Steady-state vs recovery benchmarks

### Phase 3: Memory & Observability
**Timeline:** 1 week

- [ ] Memory accounting metrics
- [ ] Buffer pool telemetry
- [ ] Extended benchmark scenarios

### Phase 4: Extension Packages
**Timeline:** 2-3 weeks

- [ ] VapeCache.Extensions.Aspire
- [ ] VapeCache.Extensions.Serilog
- [ ] VapeCache.Extensions.OpenTelemetry

---

## Next Steps

**Immediate actions:**
1. Create `docs/REDIS_PROTOCOL_SUPPORT.md` (30 min)
2. Create `docs/NON_GOALS.md` (15 min)
3. Audit cancellation paths in multiplexer (2 hours)
4. Review circuit breaker probe timeout enforcement (1 hour)

**This week:**
- Complete Phase 1 documentation (RESP, backpressure, TLS)
- Add backpressure metrics to `RedisTelemetry`
- Write failure matrix

**Next week:**
- Complete Phase 2 documentation
- Add memory accounting metrics
- Run recovery benchmarks

**By v1.0:**
- All Phase 1-3 items complete
- `VapeCache.Abstractions` API frozen
- NuGet packages published
