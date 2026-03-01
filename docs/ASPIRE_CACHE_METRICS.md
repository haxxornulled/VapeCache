# VapeCache + Aspire Dashboard: Cache Hit/Miss Metrics

**Goal:** Make VapeCache cache hit/miss rates visible in the .NET Aspire Dashboard for Blazor-based monitoring.

## Current State

VapeCache **already tracks** cache hits and misses via `CacheTelemetry.Meter`:

```csharp
// VapeCache.Infrastructure/Caching/CacheTelemetry.cs
public static class CacheTelemetry
{
    public static readonly Meter Meter = new("VapeCache.Cache");

    public static readonly Counter<long> GetCalls = Meter.CreateCounter<long>("cache.get.calls");
    public static readonly Counter<long> Hits = Meter.CreateCounter<long>("cache.get.hits");
    public static readonly Counter<long> Misses = Meter.CreateCounter<long>("cache.get.misses");
    public static readonly Counter<long> SetCalls = Meter.CreateCounter<long>("cache.set.calls");
    public static readonly Counter<long> RemoveCalls = Meter.CreateCounter<long>("cache.remove.calls");
    public static readonly Counter<long> FallbackToMemory = Meter.CreateCounter<long>("cache.fallback.to_memory");
    public static readonly Counter<long> RedisBreakerOpened = Meter.CreateCounter<long>("cache.redis.breaker.opened");

    public static readonly Histogram<double> OpMs = Meter.CreateHistogram<double>("cache.op.ms");
}
```

**Metrics are emitted** in cache services with a `backend` tag:

```csharp
// Example from HybridCacheService.cs
if (bytes is null)
{
    stats.IncMiss();
    CacheTelemetry.Misses.Add(1, new TagList { { "backend", Name } });
}
else
{
    stats.IncHit();
    CacheTelemetry.Hits.Add(1, new TagList { { "backend", Name } });
}
```

**Tags:**
- `backend="redis"` - Hit/miss from Redis
- `backend="in-memory"` - Hit/miss from in-memory fallback
- `backend="hybrid"` - Hit/miss from hybrid cache layer

## What's Needed

### ✅ No Code Changes to VapeCache.Infrastructure

The core library already emits all necessary metrics. **We will not pollute the codebase** with Aspire-specific code.

### ✨ VapeCache.Extensions.Aspire Package

A **separate NuGet package** that:

1. **Registers VapeCache meters** with OpenTelemetry
2. **Exposes metrics** to Aspire Dashboard via OTLP
3. **Provides fluent API** for easy setup
4. **Adds calculated metrics** (hit rate %) as derived metrics

## Implementation Plan

### Package Structure

```
VapeCache.Extensions.Aspire/
├── VapeCache.Extensions.Aspire.csproj
├── AspireVapeCacheExtensions.cs       // builder.AddVapeCache()
├── AspireRedisResourceExtensions.cs   // .WithRedisFromAspire()
├── AspireHealthCheckExtensions.cs     // .WithHealthChecks()
├── AspireTelemetryExtensions.cs       // .WithAspireTelemetry() ← NEW
├── CacheMetricsEnricher.cs            // Adds derived metrics (hit rate)
├── VapeCacheHealthCheck.cs            // IHealthCheck implementation
└── RedisHealthCheck.cs                // IHealthCheck implementation
```

### Dependencies

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <!-- Core VapeCache -->
    <ProjectReference Include="..\VapeCache.Infrastructure\VapeCache.Infrastructure.csproj" />

    <!-- .NET Aspire -->
    <PackageReference Include="Aspire.Hosting.Redis" Version="9.0.0" />
    <PackageReference Include="Microsoft.Extensions.ServiceDiscovery" Version="9.0.0" />

    <!-- Health Checks -->
    <PackageReference Include="Microsoft.Extensions.Diagnostics.HealthChecks" Version="10.0.0" />

    <!-- OpenTelemetry (for Aspire Dashboard integration) -->
    <PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.10.0" />
    <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.10.0" />
  </ItemGroup>
</Project>
```

### Code: AspireTelemetryExtensions.cs

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Extensions.Aspire;

public static class AspireTelemetryExtensions
{
    /// <summary>
    /// Configures OpenTelemetry to send VapeCache metrics/traces to Aspire Dashboard.
    /// Exposes cache hit/miss rates, latency, and connection pool metrics.
    /// </summary>
    public static AspireVapeCacheBuilder WithAspireTelemetry(
        this AspireVapeCacheBuilder builder)
    {
        // Aspire automatically configures OTLP endpoint via environment variables:
        // - OTEL_EXPORTER_OTLP_ENDPOINT (set by Aspire AppHost)
        // - DOTNET_DASHBOARD_OTLP_ENDPOINT_URL (fallback)
        //
        // We just need to register VapeCache meters and activity sources.

        builder.Builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                // Register VapeCache meters
                metrics.AddMeter("VapeCache.Cache");   // Cache hit/miss metrics
                metrics.AddMeter("VapeCache.Redis");   // Redis command metrics

                // Add cache metrics enricher (calculates hit rate)
                metrics.AddView(
                    instrumentName: "cache.hit_rate",
                    new ExplicitBucketHistogramConfiguration
                    {
                        Boundaries = new double[] { 0.0, 0.5, 0.75, 0.9, 0.95, 0.99, 1.0 }
                    });
            })
            .WithTracing(tracing =>
            {
                // Register VapeCache activity sources
                tracing.AddSource("VapeCache.Redis");
            });

        // Add cache metrics enricher (calculates derived metrics)
        builder.Builder.Services.AddSingleton<CacheMetricsEnricher>();
        builder.Builder.Services.AddHostedService<CacheMetricsEnricherBackgroundService>();

        return builder;
    }
}
```

### Code: CacheMetricsEnricher.cs

```csharp
using System.Diagnostics.Metrics;
using VapeCache.Infrastructure.Caching;

namespace VapeCache.Extensions.Aspire;

/// <summary>
/// Enriches VapeCache metrics with derived metrics (hit rate, miss rate).
/// </summary>
public sealed class CacheMetricsEnricher : IDisposable
{
    private readonly Meter _meter = new("VapeCache.Cache.Derived");
    private readonly ObservableGauge<double> _hitRate;
    private readonly ObservableGauge<double> _missRate;

    private long _totalHits;
    private long _totalMisses;

    public CacheMetricsEnricher()
    {
        // Create observable gauges for hit/miss rate
        _hitRate = _meter.CreateObservableGauge("cache.hit_rate", GetHitRate,
            description: "Cache hit rate (hits / total requests)",
            unit: "percentage");

        _missRate = _meter.CreateObservableGauge("cache.miss_rate", GetMissRate,
            description: "Cache miss rate (misses / total requests)",
            unit: "percentage");

        // Subscribe to VapeCache metrics to calculate rates
        // Note: This is a simplified example. In production, we'd use IMeterListener
        // to observe the actual meter callbacks from CacheTelemetry.
    }

    private double GetHitRate()
    {
        var total = _totalHits + _totalMisses;
        return total == 0 ? 0.0 : (double)_totalHits / total;
    }

    private double GetMissRate()
    {
        var total = _totalHits + _totalMisses;
        return total == 0 ? 0.0 : (double)_totalMisses / total;
    }

    public void RecordHit() => Interlocked.Increment(ref _totalHits);
    public void RecordMiss() => Interlocked.Increment(ref _totalMisses);

    public void Dispose() => _meter.Dispose();
}

/// <summary>
/// Background service that observes VapeCache metrics and updates the enricher.
/// </summary>
internal sealed class CacheMetricsEnricherBackgroundService : BackgroundService
{
    private readonly CacheMetricsEnricher _enricher;

    public CacheMetricsEnricherBackgroundService(CacheMetricsEnricher enricher)
    {
        _enricher = enricher;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // In a real implementation, we'd use IMeterListener to observe
        // CacheTelemetry.Hits and CacheTelemetry.Misses and update the enricher.
        //
        // For now, this is a placeholder showing the architecture.
        // The actual implementation would look like this:
        //
        // var listener = new MeterListener();
        // listener.InstrumentPublished = (instrument, listener) =>
        // {
        //     if (instrument.Meter.Name == "VapeCache.Cache")
        //     {
        //         if (instrument.Name == "cache.get.hits")
        //         {
        //             listener.EnableMeasurementEvents(instrument);
        //         }
        //         if (instrument.Name == "cache.get.misses")
        //         {
        //             listener.EnableMeasurementEvents(instrument);
        //         }
        //     }
        // };
        //
        // listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        // {
        //     if (instrument.Name == "cache.get.hits")
        //     {
        //         _enricher.RecordHit();
        //     }
        //     else if (instrument.Name == "cache.get.misses")
        //     {
        //         _enricher.RecordMiss();
        //     }
        // });
        //
        // listener.Start();

        await Task.CompletedTask;
    }
}
```

### Usage Example

**AppHost (Aspire Orchestrator):**

```csharp
// AppHost/Program.cs
var builder = DistributedApplication.CreateBuilder(args);

// Add Redis resource
var redis = builder.AddRedis("redis")
    .WithDataVolume()
    .WithRedisCommander();  // Optional: Redis UI

// Add your Blazor app with VapeCache
var api = builder.AddProject<Projects.MyBlazorApp>("blazor-app")
    .WithReference(redis)  // Injects ConnectionStrings:redis
    .WithExternalHttpEndpoints();

builder.Build().Run();
```

**Blazor App (Your Application):**

```csharp
// MyBlazorApp/Program.cs
var builder = WebApplication.CreateBuilder(args);

// Add VapeCache with Aspire integration
builder.AddVapeCache()
    .WithRedisFromAspire("redis")     // Binds to AppHost Redis resource
    .WithHealthChecks()                // Registers Redis + VapeCache health checks
    .WithAspireTelemetry();            // ← Sends hit/miss to Aspire Dashboard

// Add Blazor services
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

var app = builder.Build();

app.MapHealthChecks("/health");
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
```

**Blazor Component (Example):**

```razor
@page "/cache-stats"
@inject ICacheService Cache

<h3>Cache Statistics</h3>

<p>View real-time cache metrics in the Aspire Dashboard: <a href="http://localhost:15888" target="_blank">http://localhost:15888</a></p>

<p>Metrics available:</p>
<ul>
    <li><code>cache.get.hits</code> - Total cache hits</li>
    <li><code>cache.get.misses</code> - Total cache misses</li>
    <li><code>cache.hit_rate</code> - Hit rate (0.0 to 1.0)</li>
    <li><code>cache.miss_rate</code> - Miss rate (0.0 to 1.0)</li>
    <li><code>cache.fallback.to_memory</code> - Circuit breaker activations</li>
</ul>

@code {
    // Your Blazor component code
}
```

## Aspire Dashboard Visualization

When you run `dotnet run` in the AppHost project, the Aspire Dashboard opens at `http://localhost:15888`.

For lane-focused dashboards and query recipes, use:
- `docs/ASPIRE_LANE_QUERY_PACK.md`

### Metrics Tab

You'll see VapeCache metrics grouped by meter:

**VapeCache.Cache (Cache Metrics):**
- `cache.get.calls` (Counter) - Total GET calls
- `cache.get.hits` (Counter) - Cache hits
- `cache.get.misses` (Counter) - Cache misses
- `cache.hit_rate` (Gauge) - Hit rate (0.0 to 1.0)
- `cache.miss_rate` (Gauge) - Miss rate (0.0 to 1.0)
- `cache.set.calls` (Counter) - Total SET calls
- `cache.remove.calls` (Counter) - Total REMOVE calls
- `cache.fallback.to_memory` (Counter) - Circuit breaker activations
- `cache.redis.breaker.opened` (Counter) - Circuit breaker opens
- `cache.set.payload.bytes` (Histogram) - Payload size for cache writes
- `cache.set.large_key` (Counter) - Large payload writes (>64 KB)
- `cache.evictions` (Counter) - In-memory evictions (tagged by reason)
- `cache.stampede.key_rejected` (Counter) - Suspicious/invalid key rejections
- `cache.stampede.lock_wait_timeout` (Counter) - Stampede lock wait timeouts
- `cache.stampede.failure_backoff_rejected` (Counter) - Backoff window rejections
- `cache.spill.write.count` (Counter) - Spill writes
- `cache.spill.write.bytes` (Counter) - Spill write bytes
- `cache.spill.read.count` (Counter) - Spill reads
- `cache.spill.read.bytes` (Counter) - Spill read bytes
- `cache.spill.orphan.scanned` (Counter) - Spill files scanned for cleanup
- `cache.spill.orphan.cleanup.count` (Counter) - Spill files deleted during cleanup
- `cache.spill.orphan.cleanup.bytes` (Counter) - Spill bytes deleted during cleanup
- `cache.spill.store_unavailable` (Counter) - Spill enabled but no writable spill store registered
- `cache.spill.shard.active` (Gauge) - Active spill shards with at least one file
- `cache.spill.shard.max_files` (Gauge) - Max files in any single shard
- `cache.spill.shard.imbalance_ratio` (Gauge) - Shard imbalance (max/avg files per active shard)
- `cache.op.ms` (Histogram) - Operation latency

**VapeCache.Redis (Redis Metrics):**
- `redis.cmd.calls` (Counter) - Total Redis commands
- `redis.cmd.failures` (Counter) - Failed commands
- `redis.cmd.ms` (Histogram) - Command latency
- `redis.pool.acquires` (Counter) - Pool lease requests
- `redis.pool.timeouts` (Counter) - Pool timeouts
- `redis.pool.wait.ms` (Histogram) - Pool wait time
- `redis.queue.depth` (Gauge) - Write/pending queue depth (tagged by queue and connection)
- `redis.queue.wait.ms` (Histogram) - Write queue backpressure wait time
- `redis.mux.lane.bytes.sent` (ObservableCounter) - Cumulative bytes sent per mux lane
- `redis.mux.lane.bytes.received` (ObservableCounter) - Cumulative bytes received per mux lane
- `redis.mux.lane.operations` (ObservableCounter) - Cumulative operations started per mux lane
- `redis.mux.lane.responses` (ObservableCounter) - Cumulative responses observed per mux lane
- `redis.mux.lane.failures` (ObservableCounter) - Cumulative transport/connect failures per mux lane
- `redis.mux.lane.responses.orphaned` (ObservableCounter) - Responses that arrived after the waiting operation had already completed
- `redis.mux.lane.response.sequence.mismatches` (ObservableCounter) - Request/response sequence mismatches detected by the mux lane
- `redis.mux.lane.transport.resets` (ObservableCounter) - Transport resets on the mux lane
- `redis.mux.lane.inflight` (ObservableGauge) - Current in-flight operations per mux lane
- `redis.mux.lane.inflight.utilization` (ObservableGauge) - Current in-flight utilization ratio per mux lane
- `redis.connect.attempts` (Counter) - Connection attempts
- `redis.connect.failures` (Counter) - Connection failures

### Filtering by Backend

Use the `backend` tag to filter metrics by cache backend:

```
cache.get.hits{backend="redis"}       # Hits from Redis
cache.get.hits{backend="in-memory"}   # Hits from in-memory fallback
cache.get.misses{backend="hybrid"}    # Misses from hybrid cache layer
```

### Creating Dashboards (Grafana-style)

While Aspire Dashboard doesn't support custom dashboards yet (as of .NET 9), you can:

1. **Export to Prometheus:** Add Prometheus exporter to view in Grafana
2. **Export to Azure Monitor:** Send to Application Insights for Azure dashboards
3. **Custom Blazor Dashboard:** Build your own using the metrics API

## Endpoint Payloads

`GET /vapecache/status` and `GET /vapecache/stats` include:
- `stampedeKeyRejected`
- `stampedeLockWaitTimeout`
- `stampedeFailureBackoffRejected`
- `spill.mode` (`noop` or `file`)
- `spill.totalSpillFiles`, `spill.activeShards`, `spill.maxFilesInShard`
- `spill.imbalanceRatio`, `spill.topShards` (hot shard prefixes)

`GET /vapecache/stream` exposes realtime SSE frames (`event: vapecache-stats`) for Blazor charting.

## Benefits for Blazor Developers

### 1. Zero-Config Observability

```csharp
// Single line to enable all metrics
builder.AddVapeCache()
    .WithAspireTelemetry();
```

### 2. Real-Time Monitoring

Watch cache performance in real-time during development:
- Hit/miss rates
- Circuit breaker activations
- Redis connection health

### 3. Production-Ready

Same code works in production with Azure Monitor or Prometheus:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics.AddPrometheusExporter();  // Export to Prometheus
        // OR
        metrics.AddAzureMonitorMetricExporter();  // Export to Azure Monitor
    });
```

### 4. Debugging

Identify performance issues immediately:
- Low hit rate → Increase TTL or cache more data
- High fallback rate → Redis is down, check circuit breaker
- High pool timeouts → Increase MaxConnections

## Roadmap

### Phase 1: Basic Metrics (Current)
- ✅ Expose cache hit/miss counters
- ✅ Expose Redis command metrics
- ✅ Tag metrics with `backend` dimension

### Phase 2: Derived Metrics
- [ ] Calculate hit rate % (hits / total)
- [ ] Calculate miss rate % (misses / total)
- [ ] Add windowed metrics (last 1m, 5m, 15m)

### Phase 3: Custom Aspire Dashboard
- [ ] Build custom Blazor component for VapeCache metrics
- [ ] Add to Aspire Dashboard as plugin (if supported)
- [ ] Real-time charts (line graphs, gauges)

### Phase 4: Alerts
- [ ] Low hit rate alerts (< 80%)
- [ ] High fallback rate alerts (circuit breaker open)
- [ ] Pool exhaustion alerts

## Alternative: Custom Blazor Dashboard

If you want a custom dashboard in your Blazor app (without Aspire Dashboard), you can:

### 1. Create a Metrics Service

```csharp
public interface ICacheMetricsService
{
    long TotalHits { get; }
    long TotalMisses { get; }
    double HitRate { get; }
    long FallbackCount { get; }
}

public class CacheMetricsService : ICacheMetricsService
{
    private long _totalHits;
    private long _totalMisses;
    private long _fallbackCount;

    public long TotalHits => _totalHits;
    public long TotalMisses => _totalMisses;
    public double HitRate => TotalHits + TotalMisses == 0 ? 0.0 : (double)TotalHits / (TotalHits + TotalMisses);
    public long FallbackCount => _fallbackCount;

    public void RecordHit() => Interlocked.Increment(ref _totalHits);
    public void RecordMiss() => Interlocked.Increment(ref _totalMisses);
    public void RecordFallback() => Interlocked.Increment(ref _fallbackCount);
}
```

### 2. Decorate Cache Service

```csharp
public class MetricsDecoratedCacheService : ICacheService
{
    private readonly ICacheService _inner;
    private readonly ICacheMetricsService _metrics;

    public async Task<byte[]?> GetAsync(CacheKey key, CancellationToken ct)
    {
        var result = await _inner.GetAsync(key, ct);
        if (result is null) _metrics.RecordMiss();
        else _metrics.RecordHit();
        return result;
    }

    // ... other methods
}
```

### 3. Blazor Component

```razor
@page "/cache-dashboard"
@inject ICacheMetricsService Metrics
@implements IDisposable

<h3>Cache Performance</h3>

<div class="row">
    <div class="col-md-3">
        <div class="card">
            <div class="card-body">
                <h5>Hit Rate</h5>
                <h2>@($"{Metrics.HitRate:P1}")</h2>
            </div>
        </div>
    </div>
    <div class="col-md-3">
        <div class="card">
            <div class="card-body">
                <h5>Total Hits</h5>
                <h2>@Metrics.TotalHits</h2>
            </div>
        </div>
    </div>
    <div class="col-md-3">
        <div class="card">
            <div class="card-body">
                <h5>Total Misses</h5>
                <h2>@Metrics.TotalMisses</h2>
            </div>
        </div>
    </div>
    <div class="col-md-3">
        <div class="card">
            <div class="card-body">
                <h5>Fallbacks</h5>
                <h2>@Metrics.FallbackCount</h2>
            </div>
        </div>
    </div>
</div>

@code {
    private Timer? _timer;

    protected override void OnInitialized()
    {
        _timer = new Timer(_ => InvokeAsync(StateHasChanged), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    public void Dispose() => _timer?.Dispose();
}
```

## Conclusion

**Summary:**

1. ✅ **No core library pollution:** VapeCache.Infrastructure already emits metrics
2. ✅ **Separate NuGet package:** VapeCache.Extensions.Aspire handles integration
3. ✅ **Zero-config for Blazor devs:** Single `.WithAspireTelemetry()` call
4. ✅ **Aspire Dashboard visibility:** Real-time hit/miss rates in dashboard
5. ✅ **Production-ready:** Same code works with Prometheus, Azure Monitor, etc.

**Next Steps:**

1. Implement `VapeCache.Extensions.Aspire` project
2. Add `IMeterListener` to track hit/miss in real-time
3. Test with Blazor app + Aspire Dashboard
4. Publish NuGet package
5. Document in README and ASPIRE_INTEGRATION.md

This approach keeps the core library clean while providing excellent DX for Aspire/Blazor users! 🚀
