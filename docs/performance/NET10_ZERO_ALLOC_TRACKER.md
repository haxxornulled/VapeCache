# .NET 10 Zero-Allocation Tracker

This tracker maps .NET 10 performance guidance to concrete VapeCache work items.

## Completed in this pass

- `VapeCache.UI`: migrated active components to code-behind partial classes.
- `VapeCache.UI`: removed per-render `OrderBy` lane sorting; sort once per refresh.
- `VapeCache.UI`: precomputed gauge display strings/styles in `OnParametersSet`.
- `VapeCache.UI`: dashboard orchestrator now resolves diagnostics provider once (no per-snapshot LINQ).
- `VapeCache.Infrastructure`: removed string-allocation fallbacks in RESP numeric parsing hot paths (`ParseCursor`, `ParseLong`, bulk `ParseDouble`).
- `VapeCache.Infrastructure`: converted `RedisCommandExecutor` hot-path logs to source-generated `LoggerMessage`.
- `VapeCache.Infrastructure`: converted `RedisConnectionPool` logs to source-generated `LoggerMessage` and added `IsEnabled` guards around expensive argument construction.
- `VapeCache.Infrastructure`: converted `HybridCommandExecutor` fallback/retry logs to source-generated `LoggerMessage` with shared templates.
- `VapeCache.Infrastructure`: converted `HybridCacheService` breaker/reconciliation/error logs to source-generated `LoggerMessage`.
- `VapeCache.Infrastructure`: converted `RedisConnectionFactory` connect/ACL/HELLO logs to source-generated `LoggerMessage`.
- `VapeCache.Infrastructure`: converted `InMemoryCacheService`, `JsonCacheService`, and `RedisSearchService` to source-generated `LoggerMessage`.
- `VapeCache.Infrastructure`: removed `CA1848`/`CA1873` logging analyzer warnings in infra connection/cache/reconciliation paths.
- `VapeCache.Infrastructure`: tightened TLS invalid-cert callbacks to non-production-only validation paths (no unconditional `=> true` callback).
- `VapeCache.Reconciliation`: converted `RedisReconciliationService` logs to source-generated `LoggerMessage`.
- `VapeCache.Reconciliation`: converted `RedisReconciliationReaper` logs to source-generated `LoggerMessage`.
- `VapeCache.Tests`: added scripted coverage for special doubles (`+inf`, `-inf`, `NaN`) and SCAN cursor parsing.

## High-priority next passes

1. Logger hot paths (`CA1848`, `CA1873`)
- Keep infrastructure/reconciliation on source-generated `LoggerMessage` only (direct `Log*` calls removed).
- Next targets: non-logging analyzer backlog (`CA1068`, `CA1861`, `CA1512`, `CA1001`) in transport/persistence/helpers.

2. JSON allocation pressure
- Replace per-call `new JsonSerializerOptions(...)` in tests/bench harness with cached static options.
- Audit production code for repeated options creation and ensure source-generated contexts are used.

3. Collection/LINQ allocations in hot paths
- Replace hot `.Select/.Where/.ToArray()` usage with loops/spans in runtime paths.
- Keep LINQ in tests unless it impacts perf-gate correctness.

4. RESP and transport parsing
- Evaluate `SearchValues<byte>` for RESP token scanning where it improves throughput on long frames.
- Keep current parser APIs allocation-free and add perf-gate benchmarks before/after each parser change.

5. Blazor presentation discipline
- Keep page/component logic in `.razor.cs`; reserve inline `@code` for trivial one-off markup state.
- Avoid `HttpContext` coupling in UI components; use orchestrators/services only.

## Validation gate for each perf PR

1. `dotnet build` affected projects.
2. Relevant `dotnet test` subset.
3. `VapeCache.Benchmarks` run for changed feature area.
4. Perf-gate delta recorded (ops/sec, p95, alloc/op).
