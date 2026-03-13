# Settings Reference

Generated from source (*Options.cs) on 2026-03-08 16:16:10 UTC.

This reference is source-of-truth for every options setting and default currently implemented.

## RedisStressOptions

- Namespace: VapeCache.Console.Stress
- Source: VapeCache.Console/Stress/RedisStressOptions.cs

| Setting | Type | Default | Description |
|---|---|---|---|
| Enabled | bool | true | (No XML summary.) |
| Mode | string | "pool" | (No XML summary.) |
| Workers | int | 32 | (No XML summary.) |
| Duration | TimeSpan | TimeSpan.FromSeconds(30) | (No XML summary.) |
| Workload | string | "ping" | (No XML summary.) |
| PayloadBytes | int | 1024 | (No XML summary.) |
| KeySpace | int | 10_000 | (No XML summary.) |
| VirtualUsers | int | 25_000 | (No XML summary.) |
| KeyPrefix | string | "vapecache:stress:" | (No XML summary.) |
| SetPercent | int | 50 | (No XML summary.) |
| PayloadTtl | TimeSpan | TimeSpan.FromSeconds(30) | (No XML summary.) |
| PreloadKeys | bool | true | (No XML summary.) |
| TargetRps | double | 0 | (No XML summary.) |
| BurstRequests | int | 1000 | (No XML summary.) |
| OperationsPerLease | int | 1 | (No XML summary.) |
| LogEvery | TimeSpan | TimeSpan.FromSeconds(2) | (No XML summary.) |
| OperationTimeout | TimeSpan | TimeSpan.FromSeconds(2) | (No XML summary.) |
| BurnConnectionsTarget | int | 0 | (No XML summary.) |
| BurnLogEvery | int | 100 | (No XML summary.) |

## RedisExporterMetricsOptions

Runtime options for scraping redis_exporter and projecting Redis server metrics into OpenTelemetry.

- Namespace: VapeCache.Extensions.Aspire
- Source: VapeCache.Extensions.Aspire/RedisExporterMetricsOptions.cs
- Configuration Section: VapeCache:RedisExporter

| Setting | Type | Default | Description |
|---|---|---|---|
| Endpoint | string | DefaultEndpoint | Full redis_exporter metrics endpoint URI. |
| PollInterval | TimeSpan | TimeSpan.FromSeconds(5) | Polling cadence for exporter scrapes. |
| RequestTimeout | TimeSpan | TimeSpan.FromSeconds(2) | Per-request timeout for exporter scrapes. |

## RedisPubSubOptions

Controls Redis pub/sub delivery behavior.

- Namespace: VapeCache.Abstractions.Connections
- Source: VapeCache.Abstractions/Connections/RedisPubSubOptions.cs
- Configuration Section: RedisPubSub

| Setting | Type | Default | Description |
|---|---|---|---|
| Enabled | bool | true | Enables Redis pub/sub service registration and message processing. |
| DeliveryQueueCapacity | int | 512 | Per-subscription delivery queue capacity before backpressure handling applies. |
| DropOldestOnBackpressure | bool | true | When true, drops oldest queued message first when the queue is full; otherwise drops newest. |
| ReconnectDelayMin | TimeSpan | TimeSpan.FromMilliseconds(250) | Initial delay before reconnecting subscriber connection after failures. |
| ReconnectDelayMax | TimeSpan | TimeSpan.FromSeconds(5) | Maximum reconnect backoff delay for subscriber connection retries. |

## VapeCacheEndpointOptions

Options for automatic mapping of VapeCache operational endpoints.

- Namespace: VapeCache.Extensions.Aspire
- Source: VapeCache.Extensions.Aspire/VapeCacheEndpointOptions.cs

| Setting | Type | Default | Description |
|---|---|---|---|
| Enabled | bool | false | Whether endpoint auto-mapping is enabled. |
| Prefix | string | "/vapecache" | Route prefix for read-only diagnostics endpoints. |
| IncludeBreakerControlEndpoints | bool | false | When true, map breaker control endpoints under `AdminPrefix`. |
| AdminPrefix | string | "/vapecache/admin" | Route prefix for admin-only breaker controls. |
| RequireAuthorizationOnAdminEndpoints | bool | false | When true, apply `RequireAuthorization()` to auto-mapped admin control endpoints. |
| AdminAuthorizationPolicy | string? | null | Optional policy name applied to auto-mapped admin control endpoints. |
| IncludeIntentEndpoints | bool | false | Whether intent inspection endpoints are mapped. |
| EnableLiveStream | bool | false | Whether the live SSE stream endpoint is mapped. |
| EnableDashboard | bool | false | Whether the built-in dashboard endpoint is mapped. |
| LiveSampleInterval | TimeSpan | TimeSpan.FromSeconds(1) | Sampling interval for live metrics feed. |
| LiveChannelCapacity | int | 256 | Bounded channel capacity for live metrics samples. |

## PosSearchDemoOptions

- Namespace: VapeCache.Console.Pos
- Source: VapeCache.Console/Pos/PosSearchDemoOptions.cs

| Setting | Type | Default | Description |
|---|---|---|---|
| Enabled | bool | false | (No XML summary.) |
| StopHostOnCompletion | bool | true | (No XML summary.) |
| SqlitePath | string | "%LOCALAPPDATA%\\VapeCache\\pos\\catalog.db" | (No XML summary.) |
| SeedIfEmpty | bool | true | (No XML summary.) |
| SeedProductCount | int | 2_000 | (No XML summary.) |
| RedisIndexName | string | "idx:pos:catalog" | (No XML summary.) |
| RedisKeyPrefix | string | "pos:sku:" | (No XML summary.) |
| TopResults | int | 10 | (No XML summary.) |
| CashierQuery | string | "pencil" | (No XML summary.) |
| LookupCode | string | "PCL-0001" | (No XML summary.) |
| LookupUpc | string | "012345678901" | (No XML summary.) |

## PosSearchLoadOptions

- Namespace: VapeCache.Console.Pos
- Source: VapeCache.Console/Pos/PosSearchLoadOptions.cs

| Setting | Type | Default | Description |
|---|---|---|---|
| Enabled | bool | false | (No XML summary.) |
| StopHostOnCompletion | bool | true | (No XML summary.) |
| Duration | TimeSpan | TimeSpan.FromMinutes(2) | (No XML summary.) |
| Concurrency | int | 256 | (No XML summary.) |
| LogEvery | TimeSpan | TimeSpan.FromSeconds(5) | (No XML summary.) |
| TargetShoppersPerSecond | int | 0 | (No XML summary.) |
| EnableAutoRamp | bool | false | (No XML summary.) |
| RampSteps | string | "1600,2000,2400,2800" | (No XML summary.) |
| RampStepDuration | TimeSpan | TimeSpan.FromSeconds(20) | (No XML summary.) |
| StopOnFirstUnstable | bool | true | (No XML summary.) |
| TreatOpenCircuitAsUnstable | bool | true | (No XML summary.) |
| MaxFailurePercent | double | 0.5d | (No XML summary.) |
| MaxP95Ms | double | 30d | (No XML summary.) |
| HotQuery | string | "code:TV-0099" | (No XML summary.) |
| HotQueryPercent | int | 90 | (No XML summary.) |
| CashierQueryPercent | int | 7 | (No XML summary.) |
| LookupUpcPercent | int | 3 | (No XML summary.) |
| LatencySampleSize | int | 8192 | (No XML summary.) |

## RedisSecretOptions

- Namespace: VapeCache.Console.Secrets
- Source: VapeCache.Console/Secrets/RedisSecretOptions.cs

| Setting | Type | Default | Description |
|---|---|---|---|
| EnvVar | string | "VAPECACHE_REDIS_CONNECTIONSTRING" | (No XML summary.) |

## VapeCacheStartupWarmupOptions

Startup warmup and readiness options for Aspire-hosted VapeCache services.

- Namespace: VapeCache.Extensions.Aspire
- Source: VapeCache.Extensions.Aspire/VapeCacheStartupWarmupOptions.cs

| Setting | Type | Default | Description |
|---|---|---|---|
| Enabled | bool | false | Enables startup warmup. Disabled by default and activated when WithStartupWarmup is used. |
| ConnectionsToWarm | int | 8 | Number of pooled Redis leases to acquire and return during warmup. |
| RequiredSuccessfulConnections | int | 4 | Minimum successful warmup leases required to mark readiness healthy. |
| Timeout | TimeSpan | TimeSpan.FromSeconds(20) | Per-startup warmup timeout. |
| ValidatePing | bool | true | When true, sends PING on each warmed lease to validate server responsiveness. |
| FailFastOnWarmupFailure | bool | false | When true, throws during startup if readiness is not achieved. |

## CacheInvalidationOptions

Runtime options for policy-driven cache invalidation execution.

- Namespace: VapeCache.Features.Invalidation
- Source: VapeCache.Features.Invalidation/CacheInvalidationOptions.cs
- Configuration Section: VapeCache:Invalidation

| Setting | Type | Default | Description |
|---|---|---|---|
| Enabled | bool | true | Enables invalidation execution. |
| EnableTagInvalidation | bool | true | Enables tag version invalidation operations. |
| EnableZoneInvalidation | bool | true | Enables zone version invalidation operations. |
| EnableKeyInvalidation | bool | true | Enables per-key removal invalidation operations. |
| Profile | CacheInvalidationProfile | CacheInvalidationProfile.SmallWebsite | Profile preset that controls strictness and concurrency defaults. |

## RedisReconciliationReaperOptions

Configuration options for the RedisReconciliationReaper background service. Controls how frequently the reaper runs to sync pending operations back to Redis.

- Namespace: VapeCache.Reconciliation
- Source: VapeCache.Reconciliation/RedisReconciliationReaperOptions.cs

| Setting | Type | Default | Description |
|---|---|---|---|
| Enabled | bool | true | Enable or disable the Reaper background service. Default: true |
| Interval | TimeSpan | TimeSpan.FromSeconds(30) | How often the Reaper runs reconciliation. Default: 30 seconds |
| InitialDelay | TimeSpan | TimeSpan.FromSeconds(10) | Initial delay before the first reconciliation run. Useful to allow application startup to complete before starting reconciliation. Default: 10 seconds |

## RedisReconciliationStoreOptions

- Namespace: VapeCache.Reconciliation
- Source: VapeCache.Reconciliation/RedisReconciliationStoreOptions.cs

| Setting | Type | Default | Description |
|---|---|---|---|
| UseSqlite | bool | true | (No XML summary.) |
| BusyTimeoutMs | int | 1000 | (No XML summary.) |
| EnablePragmaOptimizations | bool | true | (No XML summary.) |
| VacuumOnClear | bool | false | (No XML summary.) |

## VapeCacheTelemetryOptions

Telemetry options for VapeCache OpenTelemetry registration.

- Namespace: VapeCache.Extensions.Aspire
- Source: VapeCache.Extensions.Aspire/VapeCacheTelemetryOptions.cs

| Setting | Type | Default | Description |
|---|---|---|---|
| EnableOtlpExporter | bool | true | Whether to register OTLP exporters for metrics and traces. |
| UseSeqAsDefaultExporter | bool | true | When no endpoint is configured via options/config/env, use Seq OTLP endpoint fallback. |

## VapeCacheFailoverAffinityOptions

Options for emitting sticky-session affinity hints while Redis failover is active.

- Namespace: VapeCache.Extensions.AspNetCore
- Source: VapeCache.Extensions.AspNetCore/VapeCacheFailoverAffinityOptions.cs

| Setting | Type | Default | Description |
|---|---|---|---|
| Enabled | bool | true | Enables affinity hint middleware. |
| NodeId | string | $"{Environment.MachineName}:{Environment.ProcessId}" | Unique node identifier emitted in response headers/cookies. |
| NodeHeaderName | string | "X-VapeCache-Node" | Header used to expose the current failover node id. |
| StateHeaderName | string | "X-VapeCache-Failover-State" | Header used to expose current breaker/fallback state. |
| CookieName | string | "VapeCacheAffinity" | Sticky-session cookie name used for affinity hints. |
| CookieTtl | TimeSpan | TimeSpan.FromMinutes(20) | Cookie TTL used when the middleware sets affinity hints. |
| SetCookieOnlyWhenFailingOver | bool | true | Sets affinity cookies only when failover is active. |
| EmitMismatchHeader | bool | true | Emits a mismatch header when the request cookie does not match this node id. |

## VapeCacheOutputCacheStoreOptions

Configuration options for the VapeCache-backed ASP.NET Core output-cache store.

- Namespace: VapeCache.Extensions.AspNetCore
- Source: VapeCache.Extensions.AspNetCore/VapeCacheOutputCacheStoreOptions.cs

| Setting | Type | Default | Description |
|---|---|---|---|
| KeyPrefix | string | "vapecache:output" | Prefix used for all output-cache keys written by this store. |
| DefaultTtl | TimeSpan | TimeSpan.FromSeconds(30) | Default TTL used when the middleware requests a non-positive cache duration. |
| EnableTagIndexing | bool | true | Enables in-memory tag indexing to support EvictByTag operations. |

## HybridFailoverOptions

Controls how the hybrid cache keeps local in-memory fallback state warm while Redis is healthy. These settings improve failover continuity during Redis incidents.

- Namespace: VapeCache.Abstractions.Caching
- Source: VapeCache.Abstractions/Caching/HybridFailoverOptions.cs

| Setting | Type | Default | Description |
|---|---|---|---|
| MirrorWritesToFallbackWhenRedisHealthy | bool | true | Mirrors successful Redis writes into the local fallback cache. This improves continuity when the circuit opens shortly after a write. |
| WarmFallbackOnRedisReadHit | bool | true | Mirrors successful Redis read hits into the local fallback cache. This improves continuity for hot keys during failover. |
| FallbackWarmReadTtl | TimeSpan | TimeSpan.FromMinutes(2) | TTL used when warming fallback from Redis reads. |
| FallbackMirrorWriteTtlWhenMissing | TimeSpan | TimeSpan.FromMinutes(5) | TTL used when mirroring writes that do not specify a cache-entry TTL. |
| MaxMirrorPayloadBytes | int | 256 * 1024 | Maximum payload size eligible for fallback warming/mirroring. Set to 0 to disable size limits. |
| RemoveStaleFallbackOnRedisMiss | bool | true | Removes local fallback entries when Redis reports a miss for the same key. This avoids serving stale fallback data during subsequent outages. |

## InMemorySpillOptions

- Namespace: VapeCache.Abstractions.Caching
- Source: VapeCache.Abstractions/Caching/InMemorySpillOptions.cs

| Setting | Type | Default | Description |
|---|---|---|---|
| MemoryCacheSizeLimitBytes | long | 0 | Gets or sets the maximum in-memory fallback cache size budget in bytes. Set to 0 to use the default unbounded memory-cache behavior. |
| EnableSpillToDisk | bool | false | (No XML summary.) |
| SpillThresholdBytes | int | 256 * 1024 | (No XML summary.) |
| InlinePrefixBytes | int | 4096 | (No XML summary.) |
| EnableOrphanCleanup | bool | false | (No XML summary.) |
| OrphanCleanupInterval | TimeSpan | TimeSpan.FromHours(1) | (No XML summary.) |
| OrphanMaxAge | TimeSpan | TimeSpan.FromDays(7) | (No XML summary.) |

## RedisCircuitBreakerOptions

Configuration options for the Redis circuit breaker pattern. Validation is enforced at startup to ensure safe operation.

- Namespace: VapeCache.Abstractions.Caching
- Source: VapeCache.Abstractions/Caching/RedisCircuitBreakerOptions.cs

| Setting | Type | Default | Description |
|---|---|---|---|
| Enabled | bool | true | Whether the circuit breaker is enabled. When false, no fallback occurs. |
| ConsecutiveFailuresToOpen | int | 2 | Number of consecutive failures before opening the circuit. Must be at least 2 (Polly constraint). Set to 2 for near-immediate failover (recommended for developer experience). |
| BreakDuration | TimeSpan | TimeSpan.FromSeconds(10) | Duration to keep the circuit open before attempting a half-open probe. Must be greater than zero. This is the base duration - if UseExponentialBackoff is true, actual duration increases with retries. |
| HalfOpenProbeTimeout | TimeSpan | TimeSpan.FromMilliseconds(250) | Timeout for half-open probe attempts. Must be greater than zero to prevent indefinite hangs. |
| MaxConsecutiveRetries | int | 0 | Maximum number of consecutive retry attempts before giving up completely. Set to 0 for infinite retries (circuit will keep trying forever). Default: 0 (infinite retries - never give up on Redis recovery). |
| UseExponentialBackoff | bool | true | Whether to use exponential backoff for retry delays. When true, BreakDuration doubles after each failed retry (up to MaxBreakDuration). When false, BreakDuration remains constant for all retries. |
| MaxBreakDuration | TimeSpan | TimeSpan.FromMinutes(5) | Maximum break duration when using exponential backoff. Prevents exponential backoff from growing indefinitely. |
| MaxHalfOpenProbes | int | 5 | MED-3 FIX: Maximum concurrent half-open probes allowed during circuit recovery. Prevents thundering herd when circuit closes - limits simultaneous Redis attempts. Default: 5 (allows gradual recovery without overwhelming Redis) |

## CacheChunkStreamWriteOptions

Controls how payload streams are chunked and stored in cache.

- Namespace: VapeCache.Abstractions.Caching
- Source: VapeCache.Abstractions/Caching/CacheChunkStreamWriteOptions.cs

| Setting | Type | Default | Description |
|---|---|---|---|
| ChunkSizeBytes | int | DefaultChunkSizeBytes | Chunk size in bytes used when persisting stream content. |

## CacheEntryOptions

- Namespace: VapeCache.Abstractions.Caching
- Source: VapeCache.Abstractions/Caching/CacheEntryOptions.cs

| Setting | Type | Default | Description |
|---|---|---|---|
| Ttl | TimeSpan? | null | Primary constructor option. |
| Intent | CacheIntent? | null | Primary constructor option. |

## CacheStampedeOptions

- Namespace: VapeCache.Abstractions.Caching
- Source: VapeCache.Abstractions/Caching/CacheStampedeOptions.cs

| Setting | Type | Default | Description |
|---|---|---|---|
| Enabled | bool | true | (No XML summary.) |
| MaxKeys | int | 50_000 | (No XML summary.) |
| RejectSuspiciousKeys | bool | true | Reject null/empty, control-character, or overly long keys to reduce cache pollution risk. |
| MaxKeyLength | int | 512 | Maximum accepted cache key length when stampede protection is enabled. |
| LockWaitTimeout | TimeSpan | TimeSpan.FromMilliseconds(750) | Optional upper bound for waiting on a per-key single-flight lock. Set to TimeSpan.Zero to disable lock-wait timeout. |
| EnableFailureBackoff | bool | true | When enabled, failed factory executions trigger a short cooldown to prevent origin hammering. |
| FailureBackoff | TimeSpan | TimeSpan.FromMilliseconds(500) | Cooldown duration after a factory failure for a given key. |

## RedisReconciliationOptions

Configuration options for Redis reconciliation (syncing in-memory writes back to Redis after recovery).

- Namespace: VapeCache.Abstractions.Caching
- Source: VapeCache.Abstractions/Caching/RedisReconciliationOptions.cs

| Setting | Type | Default | Description |
|---|---|---|---|
| Enabled | bool | true | Whether reconciliation is enabled. When false, in-memory writes are never synced back to Redis. |
| MaxOperationAge | TimeSpan | TimeSpan.FromMinutes(5) | Maximum age of a tracked operation before it's considered stale and discarded. Operations older than this will not be synced to Redis. Default: 5 minutes. |
| MaxPendingOperations | int | 100_000 | Advisory threshold for pending operations (persisted + queued + deferred). Tracking continues past this threshold to preserve no-drop behavior; warnings are emitted when exceeded. Default: 100000. |
| MaxOperationsPerRun | int | 10_000 | Maximum number of operations processed in a single reconciliation run. Set to 0 for unlimited. Default: 10000. |
| BatchSize | int | 256 | Batch size used during reconciliation processing. Default: 256. |
| MaxRunDuration | TimeSpan | TimeSpan.FromSeconds(30) | Maximum amount of time a reconciliation run is allowed to execute. Default: 30 seconds. |
| InitialBackoff | TimeSpan | TimeSpan.FromMilliseconds(25) | Initial backoff applied after a failed operation. Default: 25ms. |
| MaxBackoff | TimeSpan | TimeSpan.FromSeconds(2) | Maximum backoff applied after repeated failures. Default: 2s. |
| BackoffMultiplier | double | 2.0 | Exponential backoff multiplier used after failures. Default: 2.0. |
| MaxConsecutiveFailures | int | 10 | Maximum number of consecutive failures allowed before stopping reconciliation early. Prevents long blocking when Redis is still unhealthy. Set to 0 to disable. Default: 10. |

## LiveDemoOptions

- Namespace: VapeCache.Console.Hosting
- Source: VapeCache.Console/Hosting/LiveDemoOptions.cs

| Setting | Type | Default | Description |
|---|---|---|---|
| Enabled | bool | true | (No XML summary.) |
| Interval | TimeSpan | TimeSpan.FromSeconds(2) | (No XML summary.) |
| Key | string | "demo:time" | (No XML summary.) |
| Ttl | TimeSpan | TimeSpan.FromSeconds(10) | (No XML summary.) |

## StartupPreflightOptions

- Namespace: VapeCache.Console.Hosting
- Source: VapeCache.Console/Hosting/StartupPreflightOptions.cs

| Setting | Type | Default | Description |
|---|---|---|---|
| FailFast | bool | true | (No XML summary.) |
| Timeout | TimeSpan | TimeSpan.FromSeconds(5) | (No XML summary.) |
| Connections | int | 1 | (No XML summary.) |
| ValidatePing | bool | true | (No XML summary.) |
| FailoverToMemoryOnFailure | bool | true | (No XML summary.) |
| SanityCheckEnabled | bool | true | (No XML summary.) |
| SanityCheckInterval | TimeSpan | TimeSpan.FromSeconds(10) | (No XML summary.) |
| SanityCheckTimeout | TimeSpan | TimeSpan.FromSeconds(2) | (No XML summary.) |
| SanityCheckRetries | int | 3 | (No XML summary.) |
| SanityCheckRetryDelay | TimeSpan | TimeSpan.FromMilliseconds(250) | (No XML summary.) |

## PluginDemoOptions

- Namespace: VapeCache.Console.Plugins
- Source: VapeCache.Console/Plugins/PluginDemoOptions.cs

| Setting | Type | Default | Description |
|---|---|---|---|
| Enabled | bool | false | (No XML summary.) |
| KeyPrefix | string | "plugin:sample" | (No XML summary.) |
| Ttl | TimeSpan | TimeSpan.FromMinutes(5) | (No XML summary.) |

## RedisConnectionOptions

- Namespace: VapeCache.Abstractions.Connections
- Source: VapeCache.Abstractions/Connections/RedisConnectionOptions.cs

| Setting | Type | Default | Description |
|---|---|---|---|
| Host | string | "" | (No XML summary.) |
| Port | int | 6379 | (No XML summary.) |
| Database | int | 0 | (No XML summary.) |
| MaxConnections | int | 64 | (No XML summary.) |
| MaxIdle | int | 64 | (No XML summary.) |
| Warm | int | 0 | (No XML summary.) |
| ConnectTimeout | TimeSpan | TimeSpan.FromSeconds(2) | (No XML summary.) |
| AcquireTimeout | TimeSpan | TimeSpan.FromSeconds(2) | (No XML summary.) |
| ValidateAfterIdle | TimeSpan | TimeSpan.FromSeconds(30) | (No XML summary.) |
| ValidateTimeout | TimeSpan | TimeSpan.FromMilliseconds(500) | (No XML summary.) |
| IdleTimeout | TimeSpan | TimeSpan.FromMinutes(5) | (No XML summary.) |
| MaxConnectionLifetime | TimeSpan | TimeSpan.FromHours(1) | (No XML summary.) |
| ReaperPeriod | TimeSpan | TimeSpan.FromSeconds(10) | (No XML summary.) |
| TransportProfile | RedisTransportProfile | RedisTransportProfile.FullTilt | Named transport profile. Set to Custom to use explicitly configured transport values. |
| EnableTcpNoDelay | bool | true | Controls Nagle's algorithm. True favors lower latency for request/response workloads. |
| TcpSendBufferBytes | int | 4 * 1024 * 1024 | Socket send buffer size in bytes. Defaults to a full-tilt profile (4MB) and can be tuned down. Set to 0 to use OS defaults/autotuning. |
| TcpReceiveBufferBytes | int | 4 * 1024 * 1024 | Socket receive buffer size in bytes. Defaults to a full-tilt profile (4MB) and can be tuned down. Set to 0 to use OS defaults/autotuning. |
| EnableTcpKeepAlive | bool | true | (No XML summary.) |
| TcpKeepAliveTime | TimeSpan | TimeSpan.FromSeconds(30) | (No XML summary.) |
| TcpKeepAliveInterval | TimeSpan | TimeSpan.FromSeconds(10) | (No XML summary.) |
| AllowAuthFallbackToPasswordOnly | bool | false | (No XML summary.) |
| LogWhoAmIOnConnect | bool | false | (No XML summary.) |
| MaxBulkStringBytes | int | 16 * 1024 * 1024 | Maximum allowed size for Redis bulk strings (RESP protocol). Prevents DoS attacks where malicious Redis server sends extremely large bulk strings. Default: 16MB. Set to -1 for unlimited (not recommended for production). |
| MaxArrayDepth | int | 64 | Maximum nesting depth for Redis arrays (RESP protocol). Prevents stack overflow from pathological deeply-nested array responses. Default: 64 levels. Set to -1 for unlimited (not recommended). |
| RespProtocolVersion | int | 2 | RESP protocol version negotiated during connection setup. Supported values: 2 or 3. |
| EnableClusterRedirection | bool | false | Enables cluster redirect handling for MOVED/ASK responses on cache-path commands. |
| MaxClusterRedirects | int | 3 | Maximum number of redirect hops allowed for a single command. |

## RedisMultiplexerOptions

- Namespace: VapeCache.Abstractions.Connections
- Source: VapeCache.Abstractions/Connections/RedisMultiplexerOptions.cs

| Setting | Type | Default | Description |
|---|---|---|---|
| Connections | int | Math.Max(2, Environment.ProcessorCount / 2) | (No XML summary.) |
| MaxInFlightPerConnection | int | 4096 | (No XML summary.) |
| TransportProfile | RedisTransportProfile | RedisTransportProfile.FullTilt | Named transport profile. Set to Custom to use explicitly configured coalescing values. |
| EnableCommandInstrumentation | bool | false | Enables OpenTelemetry command metrics and distributed tracing. Leave disabled on strict zero-allocation hot paths unless command-level telemetry is required. Metrics: redis.cmd.calls, redis.cmd.failures, redis.cmd.ms, redis.bytes.sent/received Traces: Activity spans for each command with db.system=redis tags Default: false (favor hot-path allocation stability; opt in when needed) |
| EnableCoalescedSocketWrites | bool | true | Enables scatter/gather coalesced writes via SocketAsyncEventArgs when available. Uses direct non-coalesced sends when false. |
| EnableSocketRespReader | bool | false | Enables the experimental socket-native RESP reader instead of the stream-based reader. Keep disabled unless explicitly validating this path in your environment. |
| UseDedicatedLaneWorkers | bool | false | Runs each mux lane reader/writer loop using LongRunning worker scheduling to reduce thread-pool contention under extreme sustained load. Keep disabled by default. |
| CoalescedWriteMaxBytes | int | 512 * 1024 | Maximum bytes to include in one coalesced socket write batch. Defaults to a tuned full-tilt profile (512KB). Decrease for lower single-command latency. |
| CoalescedWriteMaxSegments | int | 192 | Maximum segment count to include in one coalesced write batch. Defaults to a tuned full-tilt profile (192 segments). |
| CoalescedWriteSmallCopyThresholdBytes | int | 1536 | Segments up to this size are copied into scratch buffers to reduce scatter/gather overhead. Defaults to a tuned full-tilt profile (1536B). |
| EnableAdaptiveCoalescing | bool | true | Enables adaptive coalescing. Low queue depths bias for latency, high depths bias for throughput. |
| AdaptiveCoalescingLowDepth | int | 6 | Queue depth at or below this value uses the adaptive minimum limits. |
| AdaptiveCoalescingHighDepth | int | 56 | Queue depth at or above this value uses the configured max coalescing limits. |
| AdaptiveCoalescingMinWriteBytes | int | 64 * 1024 | Minimum bytes used when adaptive coalescing is in low-depth mode. |
| AdaptiveCoalescingMinSegments | int | 48 | Minimum segment count used when adaptive coalescing is in low-depth mode. |
| AdaptiveCoalescingMinSmallCopyThresholdBytes | int | 384 | Minimum scratch-copy threshold used when adaptive coalescing is in low-depth mode. |
| CoalescingEnterQueueDepth | int | 8 | Queue depth that enables burst coalescing mode. |
| CoalescingExitQueueDepth | int | 3 | Queue depth that exits burst coalescing mode. Must be less than or equal to CoalescingEnterQueueDepth. |
| CoalescedWriteMaxOperations | int | 128 | Maximum pending operations included in a single coalesced write batch. |
| CoalescingSpinBudget | int | 8 | Spin iterations used to catch burst followers after the first coalesced dequeue. |
| ResponseTimeout | TimeSpan | TimeSpan.FromSeconds(2) | Maximum time to wait for a Redis response before treating the connection as unhealthy. Set to TimeSpan.Zero to disable. |
| BulkLaneConnections | int | 1 | Number of dedicated bulk lanes used for pooled bulk responses (for example GET lease/MGET-style flows). This count is carved out of the total Connections budget. Set to 0 to disable isolation and share fast lanes. |
| AutoAdjustBulkLanes | bool | false | When true, bulk lane count is derived from BulkLaneTargetRatio and recomputed from the total lane budget. When false, BulkLaneConnections is treated as the fixed target count. |
| BulkLaneTargetRatio | double | 0.25 | Target ratio of total lanes reserved as bulk lanes when AutoAdjustBulkLanes is enabled. Example: 0.25 keeps roughly 25% of all lanes as bulk-read-write. |
| BulkLaneResponseTimeout | TimeSpan | TimeSpan.FromSeconds(5) | Response timeout applied to dedicated bulk lanes. This should generally be longer than fast-lane ResponseTimeout. |
| EnableAutoscaling | bool | false | Enables bounded autoscaling of long-lived multiplexed connections. Enterprise-only feature. |
| MinConnections | int | 4 | Minimum number of multiplexed connections to keep warm. |
| MaxConnections | int | 16 | Maximum number of multiplexed connections allowed. |
| AutoscaleSampleInterval | TimeSpan | TimeSpan.FromSeconds(1) | Sampling interval for autoscaler pressure signals. |
| ScaleUpWindow | TimeSpan | TimeSpan.FromSeconds(10) | Sustained high-pressure window required before scaling up. |
| ScaleDownWindow | TimeSpan | TimeSpan.FromMinutes(2) | Sustained low-pressure window required before scaling down. |
| ScaleUpCooldown | TimeSpan | TimeSpan.FromSeconds(20) | Cooldown after a scale-up event. |
| ScaleDownCooldown | TimeSpan | TimeSpan.FromSeconds(90) | Cooldown after a scale-down event. |
| ScaleUpInflightUtilization | double | 0.75 | Scale-up threshold based on average in-flight utilization per mux (0..1). |
| ScaleDownInflightUtilization | double | 0.25 | Scale-down threshold based on average in-flight utilization per mux (0..1). |
| ScaleUpQueueDepthThreshold | int | 32 | Queue-depth threshold for scale-up pressure. |
| ScaleUpTimeoutRatePerSecThreshold | double | 2.0 | Scale-up threshold for timeout rate (timeouts/sec across pool). |
| ScaleUpP99LatencyMsThreshold | double | 40.0 | Scale-up threshold for rolling p99 latency (ms). |
| ScaleDownP95LatencyMsThreshold | double | 20.0 | Scale-down requires rolling p95 latency at or below this threshold (ms). |
| AutoscaleAdvisorMode | bool | false | Enables advisor mode. Decisions are logged but no scale actions are applied. Enterprise-only feature. |
| EmergencyScaleUpTimeoutRatePerSecThreshold | double | 8.0 | Emergency timeout-rate threshold (timeouts/sec) for immediate bounded scale-up. Enterprise-only feature. |
| ScaleDownDrainTimeout | TimeSpan | TimeSpan.FromSeconds(5) | Maximum time to wait for lane drain before removing a mux on scale-down. |
| MaxScaleEventsPerMinute | int | 2 | Maximum active scale events allowed per rolling minute. Exceeding this freezes autoscaling temporarily. |
| FlapToggleThreshold | int | 4 | Alternating up/down scale toggles required to trigger flap protection. |
| AutoscaleFreezeDuration | TimeSpan | TimeSpan.FromMinutes(2) | Freeze duration applied by guardrails (flap detection, reconnect storm, scale-rate limit). |
| ReconnectStormFailureRatePerSecThreshold | double | 2.0 | Failure-rate threshold (failures/sec across mux lanes) that triggers reconnect-storm freeze. |

## GroceryStoreStressOptions

- Namespace: VapeCache.Console.GroceryStore
- Source: VapeCache.Console/GroceryStore/GroceryStoreStressOptions.cs

| Setting | Type | Default | Description |
|---|---|---|---|
| Enabled | bool | true | (No XML summary.) |
| ConcurrentShoppers | int | 2000 | (No XML summary.) |
| TotalShoppers | int | 100000 | (No XML summary.) |
| TargetDurationSeconds | int | 180 | (No XML summary.) |
| StartupDelaySeconds | int | 5 | (No XML summary.) |
| CountdownSeconds | int | 3 | (No XML summary.) |
| BrowseChancePercent | int | 70 | (No XML summary.) |
| BrowseMinProducts | int | 10 | (No XML summary.) |
| BrowseMaxProducts | int | 25 | (No XML summary.) |
| FlashSaleJoinChancePercent | int | 30 | (No XML summary.) |
| AddToCartChancePercent | int | 50 | (No XML summary.) |
| CartItemsMin | int | 15 | (No XML summary.) |
| CartItemsMax | int | 35 | (No XML summary.) |
| CartItemQuantityMin | int | 1 | (No XML summary.) |
| CartItemQuantityMax | int | 10 | (No XML summary.) |
| ViewCartChancePercent | int | 30 | (No XML summary.) |
| CheckoutChancePercent | int | 20 | (No XML summary.) |
| RemoveFromCartChancePercent | int | 10 | (No XML summary.) |
| StatsIntervalSeconds | int | 10 | (No XML summary.) |
| StopHostOnCompletion | bool | true | (No XML summary.) |
| HotProductBiasPercent | int | 0 | (No XML summary.) |
| ForceHotProductFlashSale | bool | false | (No XML summary.) |

## Maintenance

Regenerate after changing any *Options.cs file:

```powershell
.\\tools\\Generate-SettingsReference.ps1
```

