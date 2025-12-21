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

### Local dev without committing secrets
- Keep `VapeCache.Console/appsettings.json` sanitized.
- Create `VapeCache.Console/appsettings.Development.json` (gitignored) with:
  - `"RedisSecret": { "EnvVar": "VAPECACHE_REDIS_CONNECTIONSTRING", "Required": true }`
- Set only the secret env var:
  - `$env:VAPECACHE_REDIS_CONNECTIONSTRING = 'redis://user:pass@192.168.100.125:6379/0'`

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

## Configuration (appsettings.json)
The console host is the current composition root and uses these sections (all can be overridden via env vars).

### RedisConnection
Controls socket creation, pooling and handshake behavior.
- `RedisConnection:ConnectionString`: full `redis://` or `rediss://` URI (recommended for KeyVault/secrets); when set it overrides host/port/user/pass/db/TLS fields.
- `RedisConnection:Host`, `RedisConnection:Port`: target endpoint.
- `RedisConnection:Username`, `RedisConnection:Password`, `RedisConnection:Database`: auth + db selection.
- `RedisConnection:UseTls`, `RedisConnection:TlsHost`, `RedisConnection:AllowInvalidCert`: TLS settings.
- `RedisConnection:MaxConnections`: max sockets (pool mode).
- `RedisConnection:MaxIdle`: max idle sockets retained.
- `RedisConnection:Warm`: connections to pre-create on startup (pool).
- `RedisConnection:ConnectTimeout`: connect + TLS + handshake timeout.
- `RedisConnection:AcquireTimeout`: pool lease timeout.
- `RedisConnection:ValidateAfterIdle`: validate-on-borrow if idle > this duration.
- `RedisConnection:ValidateTimeout`: timeout for validation PING.
- `RedisConnection:IdleTimeout`: drop idle connections older than this.
- `RedisConnection:MaxConnectionLifetime`: drop connections older than this.
- `RedisConnection:ReaperPeriod`: pool reaper loop interval.
- `RedisConnection:EnableTcpKeepAlive`, `RedisConnection:TcpKeepAliveTime`, `RedisConnection:TcpKeepAliveInterval`: OS keepalive tuning.
- `RedisConnection:LogWhoAmIOnConnect`: runs `ACL WHOAMI` on connect (debug; adds chatter).

### RedisSecret
Controls how the host pulls the connection string from environment (so secrets never land in git).
- `RedisSecret:EnvVar`: name of env var holding the full Redis connection string (default `VAPECACHE_REDIS_CONNECTIONSTRING`).
- `RedisSecret:Required`: if `true`, the app throws on startup when the env var is not set.

### StartupPreflight
Startup validation + failover policy.
- `StartupPreflight:Enabled`: enables preflight.
- `StartupPreflight:FailFast`: if `true`, app fails startup when Redis is unavailable.
- `StartupPreflight:Timeout`: overall preflight timeout.
- `StartupPreflight:Connections`: number of concurrent connect attempts.
- `StartupPreflight:ValidatePing`: send `PING` after connect/auth/select.
- `StartupPreflight:FailoverToMemoryOnFailure`: if `FailFast=false`, forces Redis off and uses memory until sanity check succeeds.
- `StartupPreflight:SanityCheckEnabled`: periodic background check to re-enable Redis when it comes back.
- `StartupPreflight:SanityCheckInterval`: how often to probe.
- `StartupPreflight:SanityCheckTimeout`: per-probe timeout.

### RedisCircuitBreaker
Runtime breaker in `HybridCacheService` (stops hammering Redis during outages).
- `RedisCircuitBreaker:Enabled`
- `RedisCircuitBreaker:ConsecutiveFailuresToOpen`
- `RedisCircuitBreaker:BreakDuration`
- `RedisCircuitBreaker:HalfOpenProbeTimeout`

### CacheStampede
Stampede protection wrapper over the cache.
- `CacheStampede:Enabled`
- `CacheStampede:MaxKeys`: max lock-map entries before fail-open.

### RedisMultiplexer
Controls ordered pipelining/multiplexing (used by `RedisCommandExecutor`).
- `RedisMultiplexer:Connections`: number of multiplexed connections.
- `RedisMultiplexer:MaxInFlightPerConnection`: max concurrent in-flight commands per connection.

### RedisStress
Console stress runner options.
- `RedisStress:Enabled`
- `RedisStress:Mode`: `pool` or `mux` (and `burn` when enabled).
- `RedisStress:Workers`: worker count for pool mode.
- `RedisStress:Duration`
- `RedisStress:Workload`: `ping` or `payload` (depends on mode).
- `RedisStress:PayloadBytes`, `RedisStress:PayloadTtl`, `RedisStress:SetPercent`
- `RedisStress:VirtualUsers`, `RedisStress:KeySpace`, `RedisStress:KeyPrefix`, `RedisStress:PreloadKeys`
- `RedisStress:TargetRps`, `RedisStress:BurstRequests`, `RedisStress:OperationsPerLease`
- `RedisStress:LogEvery`
- `RedisStress:BurnConnectionsTarget`, `RedisStress:BurnLogEvery`

### Web
Minimal HTTP host for Postman verification.
- `Web:Enabled`
- `Web:Urls`

### LiveDemo
Background demo loop that exercises `GetOrSetAsync` and logs backend/breaker state.
- `LiveDemo:Enabled`
- `LiveDemo:Interval`
- `LiveDemo:Key`
- `LiveDemo:Ttl`

### Serilog
Logging + sinks.
- `Serilog:MinimumLevel:*` controls verbosity.
- `Serilog:WriteTo`: console/Seq sinks.
- `Serilog:Enrich`: includes span correlation (`WithSpan`) when enabled.

### OpenTelemetry
OTLP exporter endpoint.
- `OpenTelemetry:Otlp:Endpoint`

## OpenTelemetry + Serilog
Metrics:
- Redis: meter `VapeCache.Redis`
- Cache: meter `VapeCache.Cache` (hit/miss/fallback + latencies)

Tracing:
- Redis command spans from `VapeCache.Redis`
- Serilog includes `{TraceId}:{SpanId}` via `Serilog.Enrichers.Span`

## Architecture (high level)
```mermaid
%%{init: {
  "theme": "base",
  "themeVariables": {
    "fontFamily": "Segoe UI, Inter, Arial",
    "primaryColor": "#0b1220",
    "primaryTextColor": "#e6edf3",
    "primaryBorderColor": "#2f81f7",
    "lineColor": "#8b949e",
    "secondaryColor": "#0f172a",
    "tertiaryColor": "#111827"
  }
}}%%
flowchart TB
  %% Keep the top-right area relatively empty (GitHub's diagram controls live there).

  classDef api fill:#0b1220,stroke:#2f81f7,color:#e6edf3,stroke-width:2px;
  classDef svc fill:#0f172a,stroke:#22c55e,color:#e6edf3,stroke-width:2px;
  classDef redis fill:#111827,stroke:#f97316,color:#e6edf3,stroke-width:2px;
  classDef mem fill:#111827,stroke:#a855f7,color:#e6edf3,stroke-width:2px;
  classDef telem fill:#0b1220,stroke:#eab308,color:#e6edf3,stroke-width:2px;
  classDef host fill:#0b1220,stroke:#38bdf8,color:#e6edf3,stroke-width:2px;

  subgraph Host["Console Host (Composition Root)"]
    direction TB
    App["Your App / Host"]:::host
    DI["DI + config (Autofac + appsettings)"]:::host
    Web["HTTP endpoints (Postman)"]:::host
    OTel["OpenTelemetry exporters"]:::host
    App --> DI
    App --> Web
    App --> OTel
  end

  subgraph API["VapeCache.Abstractions (NuGet boundary)"]
    direction TB
    ICache["ICacheService"]:::api
    IStats["ICacheStats"]:::api
    ICurrent["ICurrentCacheService"]:::api
    IExec["IRedisCommandExecutor"]:::api
  end

  subgraph Infra["VapeCache.Infrastructure (Implementation)"]
    direction TB
    Stampede["StampedeProtectedCacheService"]:::svc
    Hybrid["HybridCacheService (breaker + fallback)"]:::svc
    RedisCache["RedisCacheService"]:::redis
    MemCache["InMemoryCacheService"]:::mem
    Stats["CacheStats + CacheTelemetry"]:::telem
  end

  subgraph RedisWire["Redis Transport (RESP)"]
    direction TB
    Exec["RedisCommandExecutor"]:::redis
    Mux["RedisMultiplexedConnection(s)"]:::redis
    Factory["IRedisConnectionFactory"]:::redis
    Conn["RedisConnection"]:::redis
    Sock["Socket/Stream"]:::redis
  end

  DI --> ICache
  ICache --> Stampede --> Hybrid
  Hybrid -->|try| RedisCache
  Hybrid -->|fallback| MemCache

  Hybrid --> IStats
  Hybrid --> ICurrent
  RedisCache --> IExec
  IExec --> Exec --> Mux --> Factory --> Conn --> Sock

  MemCache --> IMC["IMemoryCache"]:::mem
  Stats -.-> OTel
```

Circuit breaker + fallback:
```mermaid
%%{init: {"theme":"base","themeVariables":{"fontFamily":"Segoe UI, Inter, Arial","primaryColor":"#0b1220","primaryTextColor":"#e6edf3","lineColor":"#8b949e"}}}%%
sequenceDiagram
  participant App as App
  participant S as StampedeProtectedCacheService
  participant H as HybridCacheService
  participant R as RedisCacheService
  participant M as InMemoryCacheService

  App->>S: GetAsync(key)
  S->>H: GetAsync(key)

  alt Breaker open / half-open busy
    note right of H: Fast path to memory (no Redis call)
    H->>M: GetAsync(key)
    M-->>H: value/null
  else Breaker closed (Redis allowed)
    H->>R: GetAsync(key)
    alt Redis returns value
      R-->>H: value
    else Redis returns null or throws
      R-->>H: null/throws
      note right of H: Fallback to memory on miss/error
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
