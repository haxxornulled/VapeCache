# VapeCache.Reconciliation - Usage Guide

## Overview

VapeCache.Reconciliation provides **zero data loss** during Redis outages by tracking cache writes to a local SQLite database and automatically syncing them back to Redis when it recovers.

**Enterprise Feature**: Requires Enterprise license ($499/month per organization)

---

## Quick Start

### 1. Install Package

```bash
dotnet add package VapeCache.Reconciliation
```

### 2. Basic Setup in Program.cs

```csharp
using VapeCache.Reconciliation;

var builder = WebApplication.CreateBuilder(args);

// Add VapeCache reconciliation with license key
builder.Services.AddVapeCacheRedisReconciliation(
    licenseKey: "VCENT-acme-1234567890-ABC123...",  // Your Enterprise license key
    configure: options =>
    {
        options.Enabled = true;
        options.MaxPendingOperations = 100_000;  // Max operations to track
        options.MaxOperationsPerRun = 1_000;     // Process 1K ops per reconciliation run
        options.BatchSize = 100;                 // Batch size for Redis operations
    },
    configureStore: store =>
    {
        store.UseSqlite = true;                  // Use SQLite backing store
        store.DatabasePath = "Data/reconciliation.db";
    });

// Add the Reaper background service for automatic reconciliation
builder.Services.AddReconciliationReaper(reaper =>
{
    reaper.Enabled = true;
    reaper.Interval = TimeSpan.FromSeconds(30);  // Run every 30 seconds
    reaper.InitialDelay = TimeSpan.FromSeconds(10);  // Wait 10s after startup
});

var app = builder.Build();
app.Run();
```

---

## Configuration Options

### RedisReconciliationOptions

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enabled` | `bool` | `true` | Enable/disable reconciliation |
| `MaxPendingOperations` | `int` | `100000` | Max operations to track before dropping new ones |
| `MaxOperationsPerRun` | `int` | `1000` | Max operations to process per reconciliation run |
| `BatchSize` | `int` | `100` | Batch size for SQLite and Redis operations |
| `MaxOperationAge` | `TimeSpan` | `1 hour` | Max age before operations are skipped |
| `MaxRunDuration` | `TimeSpan` | `5 minutes` | Max duration for a single reconciliation run |
| `InitialBackoff` | `TimeSpan` | `100ms` | Initial backoff delay after Redis failure |
| `MaxBackoff` | `TimeSpan` | `5 seconds` | Maximum backoff delay |
| `BackoffMultiplier` | `double` | `2.0` | Backoff multiplier for exponential backoff |
| `MaxConsecutiveFailures` | `int` | `10` | Stop reconciliation after N consecutive failures |

### RedisReconciliationStoreOptions

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `UseSqlite` | `bool` | `true` | Use SQLite (true) or in-memory (false) |
| `DatabasePath` | `string` | `"reconciliation.db"` | Path to SQLite database file |
| `BusyTimeoutMs` | `int` | `5000` | SQLite busy timeout in milliseconds |

### RedisReconciliationReaperOptions

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enabled` | `bool` | `true` | Enable/disable the Reaper background service |
| `Interval` | `TimeSpan` | `30 seconds` | How often to run reconciliation |
| `InitialDelay` | `TimeSpan` | `10 seconds` | Delay before first reconciliation run |

---

## Setup Patterns

### Pattern 1: Using appsettings.json

**appsettings.json:**
```json
{
  "RedisReconciliation": {
    "Enabled": true,
    "MaxPendingOperations": 100000,
    "MaxOperationsPerRun": 1000,
    "BatchSize": 100,
    "MaxOperationAge": "01:00:00",
    "MaxRunDuration": "00:05:00"
  },
  "RedisReconciliationStore": {
    "UseSqlite": true,
    "DatabasePath": "Data/reconciliation.db",
    "BusyTimeoutMs": 5000
  },
  "RedisReconciliationReaper": {
    "Enabled": true,
    "Interval": "00:00:30",
    "InitialDelay": "00:00:10"
  }
}
```

**Program.cs:**
```csharp
// Read from appsettings.json
builder.Services.AddVapeCacheRedisReconciliation(
    builder.Configuration,
    licenseKey: Environment.GetEnvironmentVariable("VAPECACHE_LICENSE_KEY"));

// Add Reaper with config from appsettings.json
builder.Services.AddReconciliationReaper(builder.Configuration);
```

### Pattern 2: Environment Variable License

Set the license key as an environment variable:

```bash
# Linux/macOS
export VAPECACHE_LICENSE_KEY="VCENT-acme-1234567890-ABC123..."

# Windows PowerShell
$env:VAPECACHE_LICENSE_KEY = "VCENT-acme-1234567890-ABC123..."

# Windows CMD
set VAPECACHE_LICENSE_KEY=VCENT-acme-1234567890-ABC123...
```

**Program.cs:**
```csharp
// License key will be read from VAPECACHE_LICENSE_KEY environment variable
builder.Services.AddVapeCacheRedisReconciliation();
builder.Services.AddReconciliationReaper();
```

### Pattern 3: Manual Reconciliation (No Reaper)

If you want to manually control when reconciliation runs (e.g., via a scheduled job):

```csharp
// Add reconciliation but NOT the Reaper
builder.Services.AddVapeCacheRedisReconciliation(licenseKey);

// Manually trigger reconciliation
var app = builder.Build();

// Option A: On-demand via endpoint
app.MapPost("/admin/reconcile", async (IRedisReconciliationService reconciliation) =>
{
    await reconciliation.ReconcileAsync();
    return Results.Ok("Reconciliation completed");
});

// Option B: Via scheduled job (Hangfire, Quartz, etc.)
// RecurringJob.AddOrUpdate("reconcile-redis", () => reconciliation.ReconcileAsync(), Cron.Minutely);
```

### Pattern 4: In-Memory Only (Testing)

For testing or environments where SQLite is not available:

```csharp
builder.Services.AddVapeCacheRedisReconciliation(licenseKey)
    .UseInMemoryBackingStore();  // No SQLite, operations lost on restart

builder.Services.AddReconciliationReaper();
```

---

## Integration with VapeCache

Reconciliation automatically tracks writes when the circuit breaker is OPEN:

```csharp
using VapeCache.Abstractions.Caching;

public class OrderService
{
    private readonly ICacheService _cache;

    public OrderService(ICacheService cache)
    {
        _cache = cache;
    }

    public async Task<Order?> GetOrderAsync(string orderId)
    {
        var key = $"order:{orderId}";

        // 1. Try Redis first
        var result = await _cache.GetAsync<Order>(key);
        if (result.IsSuccess)
            return result.Value;

        // 2. Redis is down, circuit breaker is OPEN
        // Cache falls back to in-memory
        // Reconciliation automatically tracks this write
        var order = await LoadOrderFromDatabaseAsync(orderId);

        if (order != null)
        {
            // This write goes to in-memory AND is tracked for reconciliation
            await _cache.SetAsync(key, order, TimeSpan.FromMinutes(10));
        }

        return order;
    }
}
```

**What happens:**
1. **Redis available**: Writes go to Redis, reconciliation does nothing
2. **Redis down (circuit breaker OPEN)**:
   - Writes go to in-memory cache
   - Reconciliation tracks the write to SQLite
3. **Redis recovers**:
   - Circuit breaker closes
   - Reaper syncs all tracked writes from SQLite back to Redis
   - Operations are removed from SQLite after successful sync

---

## Monitoring & Observability

### Check Pending Operations

```csharp
public class ReconciliationMonitor
{
    private readonly IRedisReconciliationService _reconciliation;

    public int GetPendingOperations()
    {
        return _reconciliation.PendingOperations;
    }
}
```

### OpenTelemetry Metrics

Reconciliation exposes the following metrics:

| Metric | Type | Description |
|--------|------|-------------|
| `vapecache.reconciliation.tracked` | Counter | Operations tracked (writes/deletes) |
| `vapecache.reconciliation.dropped` | Counter | Operations dropped (MaxPendingOperations reached) |
| `vapecache.reconciliation.runs` | Counter | Number of reconciliation runs |
| `vapecache.reconciliation.synced` | Counter | Operations successfully synced to Redis |
| `vapecache.reconciliation.skipped` | Counter | Operations skipped (expired or too old) |
| `vapecache.reconciliation.failed` | Counter | Operations that failed to sync |
| `vapecache.reconciliation.run_ms` | Histogram | Duration of reconciliation runs (ms) |

**Example: Prometheus queries**
```promql
# Pending operations
vapecache_reconciliation_tracked_total - vapecache_reconciliation_synced_total

# Drop rate
rate(vapecache_reconciliation_dropped_total[5m])

# Reconciliation success rate
sum(rate(vapecache_reconciliation_synced_total[5m]))
/
sum(rate(vapecache_reconciliation_tracked_total[5m]))
```

### Logging

Reconciliation logs at the following levels:

- **Information**: Reconciliation runs, operation counts
- **Warning**: Failed operations, dropped operations, max pending reached
- **Error**: Reaper errors (caught and retried)

**Example logs:**
```
[2026-01-04 15:30:00] INFO: RedisReconciliationReaper: Starting reconciliation run. Pending operations: 547
[2026-01-04 15:30:01] INFO: Redis reconciliation complete: 500 synced, 0 skipped, 0 failed
[2026-01-04 15:30:01] INFO: RedisReconciliationReaper: Reconciliation run completed. Processed: 500, Remaining: 47
```

---

## Manual Operations

### Flush All Pending Operations

```csharp
public class AdminController : ControllerBase
{
    private readonly IRedisReconciliationService _reconciliation;

    [HttpPost("reconciliation/flush")]
    public async Task<IActionResult> FlushPendingOperations()
    {
        // Clear all pending operations without syncing
        await _reconciliation.FlushAsync();
        return Ok("All pending operations cleared");
    }
}
```

### Manual Reconciliation Trigger

```csharp
[HttpPost("reconciliation/sync")]
public async Task<IActionResult> ManualReconciliation()
{
    var pendingBefore = _reconciliation.PendingOperations;
    await _reconciliation.ReconcileAsync();
    var pendingAfter = _reconciliation.PendingOperations;

    return Ok(new
    {
        ProcessedBefore = pendingBefore,
        Remaining = pendingAfter,
        Synced = pendingBefore - pendingAfter
    });
}
```

---

## Production Recommendations

### 1. SQLite Database Location

```csharp
builder.Services.AddVapeCacheRedisReconciliation(licenseKey, configureStore: store =>
{
    // Use a persistent directory
    store.DatabasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "VapeCache",
        "reconciliation.db");
});
```

### 2. Tuning for High Traffic

For applications with 10K+ writes/second during outages:

```csharp
builder.Services.AddVapeCacheRedisReconciliation(licenseKey, configure: options =>
{
    options.MaxPendingOperations = 500_000;    // Allow more pending ops
    options.MaxOperationsPerRun = 5_000;       // Process more per run
    options.BatchSize = 500;                   // Larger batches
});

builder.Services.AddReconciliationReaper(reaper =>
{
    reaper.Interval = TimeSpan.FromSeconds(10);  // Run more frequently
});
```

### 3. Alerting

Set up alerts for:
- **High pending operations** (> 50,000)
- **High drop rate** (> 100/min)
- **Reconciliation failures** (> 10/min)

---

## Troubleshooting

### Issue: "VapeCache Reconciliation is an ENTERPRISE-ONLY feature"

**Cause**: Missing or invalid license key.

**Solution**:
```csharp
// Option 1: Pass license key directly
builder.Services.AddVapeCacheRedisReconciliation(
    licenseKey: "VCENT-acme-1234567890-ABC123...");

// Option 2: Set environment variable
Environment.SetEnvironmentVariable("VAPECACHE_LICENSE_KEY", "VCENT-...");
```

### Issue: Operations not syncing to Redis

**Check**:
1. Reaper is enabled: `RedisReconciliationReaperOptions.Enabled = true`
2. Reconciliation is enabled: `RedisReconciliationOptions.Enabled = true`
3. Redis circuit breaker has closed (Redis is available)
4. Check logs for reconciliation failures

### Issue: High memory usage

**Cause**: Too many pending operations in memory (when using in-memory store).

**Solution**: Use SQLite backing store:
```csharp
builder.Services.AddVapeCacheRedisReconciliation(licenseKey)
    .UseSqliteBackingStore(store => store.DatabasePath = "reconciliation.db");
```

### Issue: SQLite database locked

**Cause**: Multiple processes accessing the same database file.

**Solution**:
1. Use separate database files per application instance
2. Increase busy timeout:
```csharp
builder.Services.AddVapeCacheRedisReconciliation(licenseKey, configureStore: store =>
{
    store.BusyTimeoutMs = 30_000;  // 30 seconds
});
```

---

## License

VapeCache.Reconciliation is an **Enterprise feature** requiring a valid license key.

**Pricing**: $499/month per organization (unlimited deployments, any Redis topology)

**Get a license**: https://vapecache.com/enterprise

**Trial**: Contact sales@vapecache.com for a 30-day trial license.

---

## Support

- **Documentation**: https://vapecache.com/docs/reconciliation
- **Enterprise Support**: support@vapecache.com (4-hour SLA)
- **GitHub Issues**: https://github.com/haxxornulled/VapeCache/issues (for OSS features only)
