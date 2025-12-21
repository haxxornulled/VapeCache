# VapeCache

Enterprise-focused caching library (in progress) with:
- Redis RESP transport (no StackExchange.Redis dependency)
- Connection pooling + optional background reaper
- Ordered protocol multiplexing (pipelining) for high throughput
- Hybrid cache (Redis + first-class in-memory fallback)
- Stampede protection + circuit breaker
- OpenTelemetry metrics + traces, and Serilog trace correlation
- Console host for live demo, stress, and HTTP verification endpoints

## Solution Layout
- `VapeCache.Abstractions`: public contracts (cache + connection abstractions)
- `VapeCache.Application`: application-layer code (future use-cases)
- `VapeCache.Infrastructure`: Redis transport/pool/multiplexer + cache implementations
- `VapeCache.Console`: current host (demo + stress + HTTP endpoints)
- `VapeCache.Tests`: unit/integration tests
- `VapeCache.Benchmarks`: BenchmarkDotNet benchmarks for hot paths

## Quickstart (Console Host)
1) Set a Redis connection string via env var:
   - PowerShell: `$env:VAPECACHE_REDIS_CONNECTIONSTRING='redis://user:pass@host:6379/0'`
2) Run:
   - `dotnet run --project VapeCache.Console -c Release`

The console host also supports a helper script that prompts for the password:
- `pwsh .\\VapeCache.Console\\run-stress-with-connectionstring.ps1`

## HTTP Endpoints (for Postman)
When `Web:Enabled=true` (default) and `Web:Urls=http://localhost:5080`:
- `GET /healthz`
- `GET /cache/current`
- `GET /cache/breaker`
- `GET /cache/stats`
- `PUT /cache/{key}?ttlSeconds=60` (body = bytes/text)
- `GET /cache/{key}`
- `DELETE /cache/{key}`
- `POST /cache/{key}/get-or-set?ttlSeconds=10` (body = text payload)

## OpenTelemetry + Serilog
Metrics:
- Redis: meter `VapeCache.Redis`
- Cache: meter `VapeCache.Cache` (hit/miss/fallback + latencies)

Tracing:
- Redis command spans from `VapeCache.Redis`
- Serilog includes `{TraceId}:{SpanId}` via `Serilog.Enrichers.Span`

## Architecture (high level)
```mermaid
flowchart LR
  App[Your App] --> Cache[ICacheService]
  Cache --> Stampede[StampedeProtectedCacheService]
  Stampede --> Hybrid[HybridCacheService]

  Hybrid -->|try| Redis[RedisCacheService]
  Hybrid -->|fallback| Mem[InMemoryCacheService]

  Redis --> Exec[IRedisCommandExecutor]
  Exec --> Mux[RedisCommandExecutor and RedisMultiplexedConnections]
  Mux --> Factory[IRedisConnectionFactory]
  Factory --> Conn[RedisConnection]
  Conn --> Socket[Socket/Stream]

  Mem --> IMC[IMemoryCache]

  Hybrid --> Current[ICurrentCacheService]
  Hybrid --> Stats[ICacheStats]
```

Circuit breaker + fallback:
```mermaid
sequenceDiagram
  participant App as App
  participant S as StampedeProtectedCacheService
  participant H as HybridCacheService
  participant R as RedisCacheService
  participant M as InMemoryCacheService

  App->>S: GetAsync(key)
  S->>H: GetAsync(key)

  alt breaker open (or half-open busy)
    H->>M: GetAsync(key)
    M-->>H: value/null
  else breaker closed
    H->>R: GetAsync(key)
    alt redis hit
      R-->>H: value
    else redis miss
      R-->>H: null
      H->>M: GetAsync(key)
      M-->>H: value/null
    else redis error
      R-->>H: throws
      H->>M: GetAsync(key)
      M-->>H: value/null
    end
  end

  H-->>S: value/null
  S-->>App: value/null
```

## Testing
- Unit/in-process tests: `dotnet test -c Release`
- Integration tests (Redis required) are skippable and can be enabled via configuration (see `VapeCache.Tests`).

## Benchmarks
- List: `dotnet run -c Release --project VapeCache.Benchmarks -- --list flat`
- Run: `dotnet run -c Release --project VapeCache.Benchmarks`

## Notes on Metrics Storage
Prefer exporting metrics via OpenTelemetry (OTLP/Prometheus/etc.) rather than writing metric series into Redis keys (cardinality + retention + write-amplification).
If you still want a Redis-backed “metrics snapshot”, do it as a coarse periodic rollup (e.g., one JSON blob per minute) rather than per-request writes.

## License
TBD
