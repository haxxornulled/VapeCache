# VapeCache Architecture

High-level architecture and design principles of VapeCache.

## Table of Contents

- [Overview](#overview)
- [Design Principles](#design-principles)
- [Component Architecture](#component-architecture)
- [Transport Layer](#transport-layer)
- [Caching Layer](#caching-layer)
- [Reliability Layer](#reliability-layer)
- [Observability Layer](#observability-layer)
- [Data Flow](#data-flow)
- [Memory Management](#memory-management)
- [Concurrency Model](#concurrency-model)
- [Performance Optimizations](#performance-optimizations)

---

## Overview

VapeCache is an **enterprise-grade Redis caching library for .NET 10** designed for:

1. **Performance**: 5-30% faster than StackExchange.Redis via ordered multiplexing + coalesced writes
2. **Reliability**: Hybrid cache with circuit breaker, stampede protection, auto-reconnect
3. **Observability**: OpenTelemetry metrics + traces, structured logging, production telemetry

**Target Use Case:** High-performance GET/SET caching in production .NET applications with Redis.

**Non-Goals:** Full Redis client (200+ commands), Pub/Sub, Lua scripting, cluster mode. See [NON_GOALS.md](NON_GOALS.md).

---

## Design Principles

### 1. Zero-Copy Where Possible

- **`RedisValueLease`**: Rent large values from `ArrayPool` without copying
- **Deterministic buffers**: Pre-allocated buffers for RESP parsing (no LOH spikes)
- **Span<T> everywhere**: Avoid string allocations in protocol layer

### 2. Hybrid Fallback for Reliability

- **Redis first**: Try Redis for all cache operations
- **In-memory fallback**: Automatic failover to `IMemoryCache` when Redis is down
- **Circuit breaker**: Stop hammering Redis during outages, auto-recover when healthy

### 3. Observable by Default

- **OpenTelemetry native**: Meter (metrics) + ActivitySource (traces) for every operation
- **Structured logging**: `ILogger<T>` with correlation IDs, trace context
- **1-2% CPU overhead**: Telemetry is cheap, troubleshooting value is massive

### 4. Explicit Over Magic

- **No reflection**: Explicit serialization via `ICacheCodecProvider`
- **No hidden state**: Connection pool, circuit breaker state are observable
- **No surprises**: Timeout behavior, retry logic, error handling are documented

### 5. Host Owns Configuration

- **IOptions<T> pattern**: Library receives configuration via DI
- **Host binds config**: `Program.cs` owns `IConfiguration`, not the library
- **Testable**: Override options in tests without appsettings.json

See [CONFIGURATION_BEST_PRACTICES.md](CONFIGURATION_BEST_PRACTICES.md) for details.

---

## Component Architecture

### High-Level Stack

```
┌─────────────────────────────────────────────────────────────┐
│ Your Application (Web API, Worker Service, Console App)    │
├─────────────────────────────────────────────────────────────┤
│ IVapeCache<TValue> (typed cache API)                       │
│   - GetAsync<TValue>(key)                                   │
│   - SetAsync<TValue>(key, value, options)                   │
│   - GetOrSetAsync<TValue>(key, factory, options)            │
├─────────────────────────────────────────────────────────────┤
│ ICacheService (low-level cache API)                        │
│   - GetAsync(key) → byte[]                                  │
│   - SetAsync(key, bytes, options)                           │
│   - RemoveAsync(key)                                        │
│   - GetOrSetAsync(key, factory, serialize, deserialize)     │
├─────────────────────────────────────────────────────────────┤
│ StampedeProtectedCacheService (coalesce concurrent requests)│
│   - AsyncKeyedLock per cache key                            │
│   - Prevents thundering herd on cache misses                │
├─────────────────────────────────────────────────────────────┤
│ HybridCacheService (circuit breaker + fallback)            │
│   ├─→ RedisCacheService (try Redis first)                  │
│   │     ↓                                                    │
│   │   RedisCommandExecutor (execute RESP commands)          │
│   │     ↓                                                    │
│   │   RedisConnectionPool (multiplexed connections)         │
│   │     ↓                                                    │
│   │   RedisMultiplexedConnection (ordered pipelining)       │
│   │     ↓                                                    │
│   │   Socket/NetworkStream/SslStream (TCP/TLS)              │
│   │                                                          │
│   └─→ InMemoryCacheService (fallback when Redis down)      │
│         ↓                                                    │
│       IMemoryCache (Microsoft.Extensions.Caching.Memory)    │
└─────────────────────────────────────────────────────────────┘
```

### Layer Responsibilities

| Layer | Responsibility | Key Components |
|-------|----------------|----------------|
| **Application** | Business logic, host entry points | Controllers, Services |
| **Typed Cache API** | Generic cache operations | `IVapeCache<T>` |
| **Cache Service** | Byte-level cache operations | `ICacheService` |
| **Stampede Protection** | Coalesce concurrent requests | `StampedeProtectedCacheService` |
| **Hybrid Fallback** | Circuit breaker + in-memory | `HybridCacheService` |
| **Redis Client** | RESP2 protocol, commands | `RedisCacheService`, `RedisCommandExecutor` |
| **Connection Pool** | Multiplexed connections | `RedisConnectionPool` |
| **Transport** | Ordered pipelining, I/O | `RedisMultiplexedConnection` |
| **Network** | TCP/TLS sockets | `Socket`, `NetworkStream`, `SslStream` |

---

## Transport Layer

### Why VapeCache is Faster (5-30% vs StackExchange.Redis)

#### 1. Ordered Multiplexing

**StackExchange.Redis:**
- Uses `TaskCompletionSource<T>` for every command
- TCS allocations: 72 bytes + closure overhead
- High GC pressure under load

**VapeCache:**
- Uses pooled `IValueTaskSource` (reusable completion sources)
- Commands queued in `Channel<RedisCommand>` (ordered FIFO)
- No TCS allocations per command

**Benchmark Result:** 8-10% faster for small payloads (32 bytes)

#### 2. Coalesced Socket Writes

**StackExchange.Redis:**
- One `Socket.SendAsync()` per command
- Syscall overhead dominates for small commands

**VapeCache:**
- Batches multiple commands into single `SendAsync()` call
- Reduces syscall count by 50-90% under load

**Implementation:**
```csharp
// CoalescedWriteBatch.cs
var batch = new CoalescedWriteBatch();
while (commandQueue.TryDequeue(out var cmd))
{
    batch.Add(cmd); // Copy command bytes to batch buffer
}
await socket.SendAsync(batch.Buffer, SocketFlags.None, ct); // Single syscall
```

**Benchmark Result:** 20-30% faster for high-throughput workloads

See [COALESCED_WRITES.md](COALESCED_WRITES.md) for deep-dive.

#### 3. Deterministic Buffer Ownership

**StackExchange.Redis:**
- Allocates response buffers on-demand
- Large values (> 85KB) hit LOH (Large Object Heap)

**VapeCache:**
- Uses `ArrayPool<byte>` for bulk replies
- Predictable memory usage, no LOH spikes

**Memory Profile:**
- StackExchange.Redis: 5-10 KB allocated/op (GC pressure)
- VapeCache: ~2.1 KB allocated/op (pooled buffers)

---

## Caching Layer

### Cache Service Hierarchy

```
ICacheService (interface)
  ├─ StampedeProtectedCacheService (decorator)
  │    └─ HybridCacheService (decorator)
  │         ├─ RedisCacheService (primary)
  │         └─ InMemoryCacheService (fallback)
  │
  └─ IVapeCache<T> (generic wrapper)
```

### StampedeProtectedCacheService

**Problem:** Thundering herd on cache miss (100 concurrent requests for same key)

**Solution:** `AsyncKeyedLock` - only first request fetches from database, others wait

**Flow:**
```
Request 1: GET user:123 (miss) → Acquire lock → Fetch from DB → SET user:123 → Release lock
Request 2: GET user:123 (miss) → Wait for lock → (Request 1 completes) → GET user:123 (hit)
Request 3: GET user:123 (miss) → Wait for lock → (Request 1 completes) → GET user:123 (hit)
```

**Implementation:**
```csharp
public async Task<TValue?> GetOrSetAsync<TValue>(
    CacheKey key,
    Func<CancellationToken, Task<TValue?>> factory,
    CacheEntryOptions options,
    CancellationToken ct)
{
    using var keyLock = await _asyncKeyedLock.LockAsync(key.ToString(), ct);

    // Try cache first (after acquiring lock)
    var cached = await _inner.GetAsync(key, ct);
    if (cached != null) return Deserialize<TValue>(cached);

    // Cache miss - call factory (only one request does this)
    var value = await factory(ct);
    if (value != null)
    {
        await _inner.SetAsync(key, Serialize(value), options, ct);
    }

    return value;
}
```

### HybridCacheService

**Problem:** Redis outages take down entire application

**Solution:** Circuit breaker + in-memory fallback

**States:**
1. **Closed**: All requests go to Redis
2. **Open**: All requests go to in-memory (Redis is down)
3. **Half-Open**: Try Redis with sanity check (recovery probe)

**Flow:**
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
        └─→ Failure → Open
```

**Configuration:**
```json
{
  "RedisCircuitBreaker": {
    "ConsecutiveFailuresToOpen": 5,
    "BreakDuration": "00:00:60",
    "HalfOpenProbeTimeout": "00:00:00.250"
  }
}
```

---

## Reliability Layer

### Connection Pool

**Why:** TCP connection reuse, warm connections, fault isolation

**Features:**
- **Min/Max pool size**: 2-10 connections (configurable)
- **Warm-up on startup**: Pre-create `MinPoolSize` connections (startup preflight)
- **Idle reaper**: Drop connections idle > 5 minutes
- **Validation**: PING command before returning connection to caller

**Acquisition Flow:**
```
1. TryRent() → Check available connections
2. If none available → Try create new connection (up to MaxConnections)
3. If at limit → Wait up to AcquireTimeout
4. If timeout → Throw TimeoutException
5. Validate connection (PING) → Return to caller
```

**Metrics:**
- `redis.pool.acquires` - How many times connection was acquired
- `redis.pool.wait.ms` - Time spent waiting for connection
- `redis.pool.timeouts` - Pool exhaustion events

### Auto-Reconnect

**Problem:** Redis restarts, network blips, firewall drops

**Solution:** Drain pending operations, release slots, reconnect on next request

**Flow:**
```
1. Command fails with IOException/SocketException
2. Mark connection as faulted
3. Drain pending commands → Complete with OperationCanceledException
4. Release connection back to pool (will be reaped)
5. Next command → Acquire new connection (auto-reconnect)
```

**No blocking:** Auto-reconnect happens lazily on next command (no reconnect loops)

---

## Observability Layer

### OpenTelemetry Integration

**Meter:** `VapeCache.Redis` (metrics)
**ActivitySource:** `VapeCache.Redis` (distributed tracing)

**Metric Categories:**
1. **Connection Metrics**: Attempts, failures, duration
2. **Pool Metrics**: Acquires, timeouts, wait time, drops
3. **Command Metrics**: Calls, failures, duration
4. **Network Metrics**: Bytes sent/received

**Trace Spans:**
```
GET user:123 (parent span - application code)
  ├─ redis.pool.acquire (acquire connection from pool)
  ├─ redis.cmd.get (execute GET command)
  │   ├─ redis.socket.send (write RESP command to socket)
  │   └─ redis.socket.receive (read RESP response from socket)
  └─ redis.pool.release (return connection to pool)
```

**Structured Logging:**
- Connection events: Connect, disconnect, reconnect
- Pool events: Acquire, release, timeout, reap
- Circuit breaker: Open, close, half-open
- Command failures: Timeout, network error, Redis error

See [OBSERVABILITY_ARCHITECTURE.md](OBSERVABILITY_ARCHITECTURE.md) for comprehensive guide.

---

## Data Flow

### Successful GET Operation

```
┌────────────────────┐
│ Application        │
│ GetAsync("user:1") │
└────────────────────┘
          │
          ↓
┌────────────────────────────────┐
│ StampedeProtectedCacheService  │
│ (no lock needed - read-only)   │
└────────────────────────────────┘
          │
          ↓
┌────────────────────────────────┐
│ HybridCacheService             │
│ (circuit closed → use Redis)   │
└────────────────────────────────┘
          │
          ↓
┌────────────────────────────────┐
│ RedisCacheService              │
│ GetAsync(key)                  │
└────────────────────────────────┘
          │
          ↓
┌────────────────────────────────┐
│ RedisCommandExecutor           │
│ ExecuteAsync(GET user:1)       │
└────────────────────────────────┘
          │
          ↓
┌────────────────────────────────┐
│ RedisConnectionPool            │
│ AcquireAsync()                 │
└────────────────────────────────┘
          │
          ↓
┌────────────────────────────────┐
│ RedisMultiplexedConnection     │
│ EnqueueCommandAsync()          │
└────────────────────────────────┘
          │
          ↓
┌────────────────────────────────┐
│ Socket                         │
│ SendAsync(*3\r\n$3\r\nGET...)  │
│ ReceiveAsync()                 │
└────────────────────────────────┘
          │
          ↓
┌────────────────────────────────┐
│ Redis Server                   │
│ Returns: $5\r\nalice\r\n       │
└────────────────────────────────┘
          │
          ↓
┌────────────────────────────────┐
│ RedisResponseParser            │
│ Parse RESP → byte[]            │
└────────────────────────────────┘
          │
          ↓
┌────────────────────────────────┐
│ Application                    │
│ Deserialize(byte[]) → User     │
└────────────────────────────────┘
```

### Failed GET with Circuit Breaker

```
┌────────────────────┐
│ Application        │
│ GetAsync("user:1") │
└────────────────────┘
          │
          ↓
┌────────────────────────────────┐
│ HybridCacheService             │
│ (circuit open → use in-memory) │
└────────────────────────────────┘
          │
          ↓
┌────────────────────────────────┐
│ InMemoryCacheService           │
│ GetAsync(key) → null           │
└────────────────────────────────┘
          │
          ↓
┌────────────────────────────────┐
│ Application                    │
│ (cache miss - fetch from DB)   │
└────────────────────────────────┘
```

---

## Memory Management

### Pooling Strategy

**ArrayPool<byte>:** Used for:
- RESP bulk reply buffers (large values)
- Coalesced write batch buffers
- Response parsing buffers

**IValueTaskSource Pooling:** Used for:
- Command completion sources (reusable TCS)
- Reduces allocations by 90% vs `TaskCompletionSource<T>`

**No Pooling:** Used for:
- Small RESP commands (< 256 bytes) - stack allocated
- Inline arrays (`Span<byte>` on stack)

### Large Object Heap (LOH) Avoidance

**Problem:** Objects > 85 KB go to LOH (never compacted, causes fragmentation)

**Solutions:**
1. **RedisValueLease**: Rent large values from `ArrayPool` (lease pattern)
2. **Chunked reads**: Read large responses in chunks (no single 1 MB allocation)
3. **Deterministic buffers**: Pre-allocate buffer pool at startup (fixed size)

**Memory Profile (BenchmarkDotNet):**
```
Method             | Mean Alloc | LOH Alloc |
-------------------|------------|-----------|
StackExchange.Redis| 5.2 KB     | 1.8 KB    |
VapeCache          | 2.1 KB     | 0 KB      |
```

---

## Concurrency Model

### Connection Multiplexing

**Single Connection:**
- `Channel<RedisCommand>` - FIFO queue of pending commands
- `Socket.SendAsync()` - Write loop (coalesced batches)
- `Socket.ReceiveAsync()` - Read loop (parse RESP responses)

**Ordering Guarantee:**
- Commands are executed in order (RESP2 requirement)
- Responses are matched to commands by position in queue

**Max In-Flight:** 4096 concurrent commands per connection (configurable)

### Thread Safety

**Thread-Safe Components:**
- `RedisConnectionPool` - Lock-free queue (`ConcurrentQueue`)
- `RedisMultiplexedConnection` - `Channel<T>` (thread-safe by design)
- `HybridCacheService` - Circuit breaker uses atomic operations

**Not Thread-Safe (By Design):**
- `RedisResponseParser` - Stateful, single-threaded per connection
- `CoalescedWriteBatch` - Used by single writer thread

---

## Performance Optimizations

### 1. Ordered Multiplexing
**Impact:** 8-10% faster
**Technique:** Pooled `IValueTaskSource` eliminates TCS allocations

### 2. Coalesced Writes
**Impact:** 20-30% faster
**Technique:** Batch commands into single `SendAsync()` call

### 3. Zero-Copy Leasing
**Impact:** 50%+ less memory
**Technique:** `RedisValueLease` rents from `ArrayPool`, no copying

### 4. Inline Parsing
**Impact:** 5-10% faster
**Technique:** Parse RESP directly into `Span<byte>` (no intermediate buffers)

### 5. Stampede Protection
**Impact:** 100x less DB load
**Technique:** Coalesce concurrent cache misses (only one DB query)

---

## See Also

- [PERFORMANCE.md](PERFORMANCE.md) - Benchmark methodology and results
- [COALESCED_WRITES.md](COALESCED_WRITES.md) - Deep-dive on socket I/O optimization
- [CONFIGURATION.md](CONFIGURATION.md) - Complete configuration reference
- [OBSERVABILITY_ARCHITECTURE.md](OBSERVABILITY_ARCHITECTURE.md) - Metrics, traces, logs
- [NON_GOALS.md](NON_GOALS.md) - Strategic boundaries and positioning
