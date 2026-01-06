# VapeCache Reconciliation Architecture

## Overview

This document explains how VapeCache's data loss mitigation works through hybrid caching with SQLite-backed reconciliation.

---

## High-Level Architecture: Normal Operation (Redis Available)

```mermaid
sequenceDiagram
    participant App as Application
    participant Cache as ICacheService
    participant CB as Circuit Breaker
    participant Redis as Redis Server
    participant Memory as In-Memory Cache

    App->>Cache: SetAsync("user:123", data)
    Cache->>CB: Check circuit state
    CB-->>Cache: CLOSED (Redis healthy)
    Cache->>Redis: SET user:123 {data}
    Redis-->>Cache: OK
    Cache-->>App: Success

    Note over Redis,Memory: In-memory cache is bypassed<br/>All operations go to Redis
```

---

## Reconciliation Flow: Redis Outage with SQLite Tracking

```mermaid
sequenceDiagram
    participant App as Application
    participant Cache as ICacheService
    participant CB as Circuit Breaker
    participant Redis as Redis Server (DOWN)
    participant Memory as In-Memory Cache
    participant Rec as Reconciliation Service
    participant SQLite as SQLite Store

    rect rgb(255, 240, 240)
        Note over Redis: Redis becomes unavailable
        App->>Cache: SetAsync("user:123", data)
        Cache->>CB: Check circuit state
        CB->>Redis: Attempt connection
        Redis--xCache: Connection failed
        CB-->>Cache: OPEN (failover to memory)

        par Write to In-Memory
            Cache->>Memory: Store "user:123" → data
            Memory-->>Cache: Stored
        and Track for Reconciliation
            Cache->>Rec: TrackWrite("user:123", data, TTL)
            Rec->>SQLite: INSERT INTO pending_ops<br/>(key, value, operation, timestamp, ttl)
            SQLite-->>Rec: Row inserted
            Rec-->>Cache: Tracked
        end

        Cache-->>App: Success (from memory)

        Note over Memory,SQLite: Write succeeded locally<br/>Pending sync to Redis
    end
```

---

## Reconciliation Reaper: Automatic Sync-Back

```mermaid
sequenceDiagram
    participant Reaper as Reconciliation Reaper<br/>(Background Service)
    participant CB as Circuit Breaker
    participant Redis as Redis Server
    participant SQLite as SQLite Store
    participant Memory as In-Memory Cache

    loop Every 30 seconds
        Reaper->>CB: Check circuit state

        alt Circuit Still OPEN
            CB-->>Reaper: OPEN (Redis still down)
            Note over Reaper: Skip this run<br/>Redis not available yet
        else Circuit CLOSED
            CB-->>Reaper: CLOSED (Redis recovered!)

            Reaper->>SQLite: SELECT * FROM pending_ops<br/>ORDER BY timestamp<br/>LIMIT 1000
            SQLite-->>Reaper: [Op1, Op2, Op3, ...]

            loop For each pending operation
                alt Operation type: SET
                    Reaper->>Redis: SET key value EX ttl
                    Redis-->>Reaper: OK
                    Reaper->>SQLite: DELETE FROM pending_ops<br/>WHERE id = {op.id}
                else Operation type: DELETE
                    Reaper->>Redis: DEL key
                    Redis-->>Reaper: OK
                    Reaper->>SQLite: DELETE FROM pending_ops<br/>WHERE id = {op.id}
                end

                Note over Reaper,SQLite: Operation synced<br/>Removed from tracking
            end

            Reaper->>Memory: Optionally clear in-memory<br/>(data now in Redis)
        end
    end
```

---

## Complete State Diagram: Circuit Breaker + Reconciliation

```mermaid
stateDiagram-v2
    [*] --> Closed: Application starts

    state Closed {
        [*] --> RedisHealthy
        RedisHealthy --> RedisHealthy: All operations go to Redis
        RedisHealthy --> [*]: Write succeeds
    }

    Closed --> Open: 2 consecutive failures

    state Open {
        [*] --> FallbackMode
        FallbackMode --> TrackWrite: Write operation
        TrackWrite --> StoreInMemory: Cache locally
        StoreInMemory --> LogToSQLite: Track for reconciliation
        LogToSQLite --> [*]: Return success to app

        FallbackMode --> ReadMemory: Read operation
        ReadMemory --> [*]: Return from in-memory
    }

    Open --> HalfOpen: After 10 seconds

    state HalfOpen {
        [*] --> ProbeRedis
        ProbeRedis --> TestConnection: Send PING
        TestConnection --> [*]: Probe result
    }

    HalfOpen --> Closed: Probe succeeds
    HalfOpen --> Open: Probe fails

    Closed --> ReconciliationActive: Reaper detects pending ops

    state ReconciliationActive {
        [*] --> LoadPendingOps
        LoadPendingOps --> ProcessBatch: Batch of 1000 ops
        ProcessBatch --> SyncToRedis: For each operation
        SyncToRedis --> RemoveFromSQLite: On success
        RemoveFromSQLite --> ProcessBatch: Next batch
        ProcessBatch --> [*]: All synced
    }

    ReconciliationActive --> Closed: Reconciliation complete
```

---

## Data Flow: Complete Write Journey During Outage

```mermaid
graph TB
    subgraph "1. Application Write"
        A[App calls SetAsync]
    end

    subgraph "2. Circuit Breaker Check"
        B{Circuit<br/>State?}
        C[Try Redis]
        D[Redis Failed]
    end

    subgraph "3. Fallback to Memory"
        E[In-Memory Cache]
        F[MemoryCache.Set]
    end

    subgraph "4. Reconciliation Tracking"
        G[Reconciliation Service]
        H[SQLite Database]
        I[INSERT pending_op]
    end

    subgraph "5. Background Sync"
        J[Reaper runs every 30s]
        K{Redis<br/>Available?}
        L[Load pending ops]
        M[Sync to Redis]
        N[DELETE from SQLite]
    end

    A --> B
    B -->|OPEN| E
    B -->|CLOSED| C
    C --> D
    D --> E

    E --> F
    F --> G
    G --> I
    I --> H

    J --> K
    K -->|Yes| L
    K -->|No| J
    L --> M
    M --> N
    N --> J

    style D fill:#ffcccc
    style E fill:#ccffcc
    style H fill:#cce5ff
    style M fill:#ffffcc
```

---

## SQLite Schema

```sql
CREATE TABLE pending_operations (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    key TEXT NOT NULL,
    value BLOB,
    operation TEXT NOT NULL,  -- 'SET' or 'DELETE'
    timestamp INTEGER NOT NULL,
    ttl_seconds INTEGER,
    created_at TEXT DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX idx_timestamp ON pending_operations(timestamp);
CREATE INDEX idx_key ON pending_operations(key);
```

---

## Architecture Components

### 1. **ICacheService** (User-facing API)
- Provides `GetAsync`, `SetAsync`, `RemoveAsync` methods
- Abstracts underlying storage (Redis or Memory)
- Transparent to the application

### 2. **Circuit Breaker**
- Monitors Redis health
- States: CLOSED (healthy), OPEN (failed), HALF-OPEN (testing)
- Triggers failover to in-memory cache
- Auto-recovery after 10 seconds

### 3. **In-Memory Cache**
- `IMemoryCache` implementation
- Acts as fallback during Redis outages
- Temporary storage until Redis recovers
- Configurable TTL and size limits

### 4. **Reconciliation Service**
- Tracks writes that occurred during outage
- Stores operations in SQLite for durability
- Provides `ReconcileAsync()` method for manual sync

### 5. **Reconciliation Reaper**
- Background service (`IHostedService`)
- Runs every 30 seconds (configurable)
- Automatically syncs pending operations when Redis recovers
- Processes operations in batches (default: 1000)

### 6. **SQLite Backing Store**
- Durable storage for pending operations
- Survives application restarts
- Configurable path (default: `reconciliation.db`)
- Transactional integrity

---

## Failure Scenarios & Edge Cases

### Scenario 1: Redis Down, App Continues Running
```mermaid
sequenceDiagram
    Note over App,SQLite: Normal operation
    Redis->>Redis: ❌ Crashes
    App->>Cache: Write operations
    Cache->>Memory: Store locally
    Cache->>SQLite: Track operations
    Note over SQLite: 1000 pending ops
    Redis->>Redis: ✅ Recovers
    Reaper->>Redis: Sync 1000 ops
    Reaper->>SQLite: Clear tracking
    Note over Redis: Data restored
```

### Scenario 2: App Crashes Before SQLite Flush
```mermaid
sequenceDiagram
    Redis->>Redis: ❌ Down
    App->>Memory: Write 10 ops
    App->>SQLite: Track 8 ops
    App->>App: ❌ CRASH (2 ops lost)
    Note over Memory,SQLite: 2 operations lost<br/>8 operations safe in SQLite
    App->>App: ✅ Restart
    Reaper->>SQLite: Load 8 pending ops
    Redis->>Redis: ✅ Recovers
    Reaper->>Redis: Sync 8 ops
    Note over Redis: 8 operations restored<br/>2 operations permanently lost
```

### Scenario 3: Disk Full / SQLite Fails
```mermaid
sequenceDiagram
    Redis->>Redis: ❌ Down
    App->>Memory: Write operation
    App->>SQLite: ❌ INSERT fails (disk full)
    SQLite-->>App: Error
    Note over App: Operation in memory only<br/>Will be lost if app restarts
    App->>App: Log warning
    App-->>User: Success (degraded mode)
```

---

## Configuration Examples

### Minimal Configuration
```csharp
builder.Services.AddVapeCacheRedisReconciliation();
builder.Services.AddReconciliationReaper();
```

### Production Configuration
```json
{
  "RedisReconciliation": {
    "Enabled": true,
    "MaxPendingOperations": 500000,
    "MaxOperationsPerRun": 5000,
    "BatchSize": 500,
    "MaxOperationAge": "01:00:00"
  },
  "RedisReconciliationStore": {
    "UseSqlite": true,
    "DatabasePath": "/var/lib/vapecache/reconciliation.db",
    "BusyTimeoutMs": 30000
  },
  "RedisReconciliationReaper": {
    "Enabled": true,
    "Interval": "00:00:10",
    "InitialDelay": "00:00:05"
  }
}
```

---

## Performance Characteristics

### Write Performance During Outage
- **In-Memory Write**: ~1-2 μs (nanosecond-scale)
- **SQLite Insert**: ~100-500 μs (microsecond-scale)
- **Total Latency**: ~500 μs (vs 1-5ms for Redis over network)
- **Throughput**: 50K-100K writes/second to SQLite

### Reconciliation Performance
- **Batch Size**: 1000 operations (configurable)
- **Sync Rate**: 10K-50K ops/second to Redis (network dependent)
- **Recovery Time**: 10-30 seconds for 100K pending operations

### Storage Requirements
- **SQLite Overhead**: ~100 bytes per operation
- **100K pending operations**: ~10 MB disk space
- **500K pending operations**: ~50 MB disk space

---

## Best Practices

### 1. **Monitor Pending Operation Count**
```csharp
var pendingCount = reconciliationService.PendingOperations;
if (pendingCount > 50000)
{
    logger.LogWarning("High pending operation count: {Count}", pendingCount);
}
```

### 2. **Use Persistent SQLite Store**
```csharp
builder.Services.AddVapeCacheRedisReconciliation(configureStore: store =>
{
    store.DatabasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "VapeCache",
        "reconciliation.db"
    );
});
```

### 3. **Set Appropriate Batch Sizes**
For high-throughput applications:
```csharp
builder.Services.AddVapeCacheRedisReconciliation(configure: options =>
{
    options.MaxOperationsPerRun = 10000;  // Process 10K ops per run
    options.BatchSize = 1000;              // 1K ops per Redis batch
});
```

### 4. **Alert on Dropped Operations**
```csharp
// Monitor OpenTelemetry metric
vapecache_reconciliation_dropped_total
```

---

## Limitations & Caveats

### What Reconciliation **CAN** Do:
✅ Mitigate data loss during **transient Redis outages**
✅ Survive **application restarts** (operations persisted in SQLite)
✅ Handle **thousands of writes per second** during outage
✅ Automatically sync back when Redis recovers

### What Reconciliation **CANNOT** Do:
❌ Guarantee zero data loss in **all scenarios**
❌ Survive **disk failures** (SQLite is on disk)
❌ Prevent loss on **immediate application crash** (unflushed writes)
❌ Handle **conflicting writes** across multiple application instances
❌ Provide **ACID guarantees** across distributed systems

---

## Comparison with Alternatives

| Feature | VapeCache Reconciliation | Write-Through Cache | Event Sourcing |
|---------|-------------------------|---------------------|----------------|
| **Data Loss Mitigation** | ✅ Yes (SQLite tracking) | ❌ No (fails if Redis down) | ✅ Yes (event log) |
| **Performance Impact** | Low (~500μs per write) | None (direct Redis) | High (log + projections) |
| **Complexity** | Medium | Low | High |
| **Recovery Time** | Seconds-minutes | N/A | Minutes-hours |
| **Storage Overhead** | ~100 bytes/op | None | High (full event history) |
| **Best For** | Mitigating transient outages | Normal operation | Full audit trail |

---

## See Also

- [VapeCache.Reconciliation Usage Guide](../VapeCache.Reconciliation/USAGE_GUIDE.md)
- [Circuit Breaker Documentation](CIRCUIT_BREAKER.md)
- [Failure Scenarios](FAILURE_SCENARIOS.md)
