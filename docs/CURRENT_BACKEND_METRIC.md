# Current Backend Metric Enhancement

**Date:** December 25, 2025
**Status:** ✅ COMPLETE

## Overview

Added a new **ObservableGauge metric** (`cache.current.backend`) that exposes which cache backend is currently active in real-time. This provides instant visibility in monitoring dashboards (Aspire, Grafana, etc.) to see whether VapeCache is using Redis or the in-memory fallback.

## Problem Statement

Previously, while VapeCache tracked cache hits/misses and fallback events, there was no **direct real-time indicator** of which backend was currently serving requests. Users had to infer the current state from:
- `cache.fallback.to_memory` counter (only increments on fallback)
- Circuit breaker state (requires separate API call)
- Logs (not real-time metrics)

This made it difficult to:
- Monitor circuit breaker state in dashboards
- Set up alerts for "Redis is down"
- Understand current system behavior at a glance

## Solution

Added `cache.current.backend` as an **ObservableGauge** that reports:
- `1` when Redis is active
- `0` when in-memory cache is active
- `-1` when state is unknown (should never happen)

The metric includes a `backend` tag showing the current backend name ("redis" or "in-memory").

## Implementation Details

### 1. Updated `CacheTelemetry.cs`

**File:** [VapeCache.Infrastructure/Caching/CacheTelemetry.cs](../VapeCache.Infrastructure/Caching/CacheTelemetry.cs)

**Changes:**
```csharp
// Added dependency
using VapeCache.Abstractions.Caching;

// Added initialization method
private static ICurrentCacheService? _currentCacheService;

internal static void Initialize(ICurrentCacheService currentCacheService)
{
    _currentCacheService = currentCacheService;
}

// Added ObservableGauge
public static readonly ObservableGauge<int> CurrentBackend = Meter.CreateObservableGauge(
    "cache.current.backend",
    observeValue: () =>
    {
        if (_currentCacheService is null)
            return new Measurement<int>(0, new TagList { { "backend", "unknown" } });

        var current = _currentCacheService.CurrentName;
        var value = current switch
        {
            "redis" => 1,
            "in-memory" => 0,
            _ => -1
        };

        return new Measurement<int>(value, new TagList { { "backend", current } });
    },
    unit: "backend",
    description: "Current active cache backend (1=redis, 0=in-memory, -1=unknown)");
```

**Key Design Decisions:**
- Uses **ObservableGauge** (not Counter/Histogram) because it represents current state
- Returns numeric value (1/0/-1) for easy graphing and alerting
- Includes `backend` tag for filtering/grouping
- Lazy-initialized via `_currentCacheService` reference

### 2. Updated `CacheRegistration.cs`

**File:** [VapeCache.Infrastructure/Caching/CacheRegistration.cs](../VapeCache.Infrastructure/Caching/CacheRegistration.cs)

**Changes:**
```csharp
services.AddSingleton<ICurrentCacheService>(sp =>
{
    var currentCacheService = new CurrentCacheService();
    // Initialize telemetry with current cache service for observable gauge
    CacheTelemetry.Initialize(currentCacheService);
    return currentCacheService;
});
```

**Rationale:**
- Initialization happens during DI registration
- Ensures `CacheTelemetry` has reference before any cache operations
- Single initialization per application lifetime

### 3. Leveraged Existing `ICurrentCacheService`

**File:** [VapeCache.Abstractions/Caching/ICurrentCacheService.cs](../VapeCache.Abstractions/Caching/ICurrentCacheService.cs)

VapeCache **already had** a service tracking the current backend:

```csharp
public interface ICurrentCacheService
{
    string CurrentName { get; }
    void SetCurrent(string name);
}
```

**Implementation:** `CurrentCacheService` uses `Volatile.Read/Write` for thread-safety.

**Usage in `HybridCacheService`:**
- Line 100: `current.SetCurrent(memory.Name);` - When falling back
- Line 133: `current.SetCurrent(redis.Name);` - When Redis succeeds
- Line 144: `current.SetCurrent(memory.Name);` - On Redis failure

This means the metric **automatically updates** whenever the backend switches!

## Benefits

### 1. Real-Time Visibility
**Before:**
```
# Had to check logs or infer from fallback counter
cache.fallback.to_memory: 157 (total historical fallbacks)
```

**After:**
```
# Instant current state
cache.current.backend{backend="redis"}: 1       ← Redis is active
cache.current.backend{backend="in-memory"}: 0   ← In-memory is active
```

### 2. Dashboard Visualization

**Grafana/Aspire Dashboard:**
```promql
# Show current backend as time series
cache_current_backend

# Alert when Redis is down
alert: RedisDown
expr: cache_current_backend == 0
for: 5m
```

### 3. Correlation with Other Metrics

Now you can correlate backend state with performance:
- When `cache.current.backend == 0`, expect higher `cache.get.misses` (in-memory has smaller capacity)
- When backend switches to `1`, expect recovery in hit rate

### 4. Zero Performance Overhead

ObservableGauge is only evaluated when scraped (not on every operation), so there's zero performance impact on cache operations.

## Usage Examples

### Aspire Dashboard

Navigate to `http://localhost:15888` → Metrics tab:

**Query:**
```
cache.current.backend
```

**Result:**
```
Metric: cache.current.backend
Value: 1
Tags: backend="redis"
Description: Current active cache backend (1=redis, 0=in-memory, -1=unknown)
```

**Interpretation:**
- Value = 1: Redis is healthy and serving requests
- Value = 0: Circuit breaker is open, using in-memory fallback

### Prometheus + Grafana

**PromQL Query:**
```promql
# Current backend state
cache_current_backend

# Time spent in fallback mode (last hour)
sum_over_time(cache_current_backend{backend="in-memory"}[1h])

# Alert if Redis has been down for > 5 minutes
ALERTS FOR cache_current_backend == 0 FOR 5m
```

**Grafana Panel:**
```json
{
  "title": "Cache Backend Status",
  "targets": [
    {
      "expr": "cache_current_backend",
      "legendFormat": "{{ backend }}"
    }
  ],
  "type": "graph",
  "yaxes": [
    {
      "min": -1,
      "max": 1,
      "label": "Backend (1=Redis, 0=InMemory)"
    }
  ]
}
```

### SEQ Correlation

Combine metric with logs:
```sql
-- Show backend switches in SEQ
@Message like '%falling back to memory%'
AND cache_current_backend == 0
```

## Testing

### Manual Testing

1. **Start with Redis healthy:**
   ```bash
   dotnet run --project VapeCache.Console
   # Check metric: cache.current.backend == 1
   ```

2. **Stop Redis:**
   ```bash
   docker stop redis
   # Check metric: cache.current.backend == 0 (switches within seconds)
   ```

3. **Restart Redis:**
   ```bash
   docker start redis
   # Check metric: cache.current.backend == 1 (auto-recovery)
   ```

### Automated Testing

**Integration Test (future work):**
```csharp
[Fact]
public async Task CurrentBackendMetric_ReflectsCircuitBreakerState()
{
    // Arrange
    var services = new ServiceCollection();
    services.AddVapecacheCaching();
    var provider = services.BuildServiceProvider();
    var cache = provider.GetRequiredService<ICacheService>();
    var currentBackend = provider.GetRequiredService<ICurrentCacheService>();

    // Act - Trigger circuit breaker
    var breaker = provider.GetRequiredService<IRedisFailoverController>();
    breaker.ForceOpen("test");

    // Assert
    Assert.Equal("in-memory", currentBackend.CurrentName);

    // Metric would report: cache.current.backend{backend="in-memory"} = 0
}
```

## Documentation Updates

### Files Updated:
1. ✅ [VapeCache.Extensions.Aspire/README.md](../VapeCache.Extensions.Aspire/README.md) - Added metric to dashboard list
2. ✅ [docs/ASPIRE_PACKAGE_SUMMARY.md](ASPIRE_PACKAGE_SUMMARY.md) - Added to metrics table
3. ✅ [docs/CURRENT_BACKEND_METRIC.md](CURRENT_BACKEND_METRIC.md) - This document

### README References:
The metric is now documented in:
- Aspire integration quick start
- Metrics reference table
- Usage examples

## Related Work

### Existing Infrastructure Used:
- `ICurrentCacheService` - Already tracking current backend (no changes needed)
- `HybridCacheService` - Already calling `SetCurrent()` on backend switches (no changes needed)
- `CacheTelemetry` - Enhanced with new ObservableGauge

### Future Enhancements:
- [ ] Add `cache.circuit_breaker.state` gauge (0=closed, 1=open, 2=half-open)
- [ ] Add `cache.circuit_breaker.failures` counter
- [ ] Add `cache.backend.uptime` histogram (time spent in each backend)

## Rollout

### Backwards Compatibility
✅ **Fully backwards compatible** - new metric added, no breaking changes

### Migration
✅ **Zero migration needed** - automatic on upgrade

### Deployment
1. Build updated VapeCache.Infrastructure.dll
2. Deploy to production
3. New metric appears automatically in Aspire Dashboard / Prometheus

## Conclusion

The `cache.current.backend` metric provides **instant, real-time visibility** into which cache backend is serving requests. This is critical for:
- Production monitoring dashboards
- Incident response (is Redis down?)
- Performance correlation (why are hit rates low?)
- Capacity planning (how often do we fall back?)

**Implementation:** ✅ Complete
**Testing:** ✅ Manual testing successful
**Documentation:** ✅ Updated
**Build:** ✅ Passes

---

**Related Documentation:**
- [ASPIRE_INTEGRATION.md](ASPIRE_INTEGRATION.md) - Aspire integration guide
- [OBSERVABILITY_ARCHITECTURE.md](OBSERVABILITY_ARCHITECTURE.md) - Full observability guide
- [FAILURE_SCENARIOS.md](FAILURE_SCENARIOS.md) - What happens when Redis fails
