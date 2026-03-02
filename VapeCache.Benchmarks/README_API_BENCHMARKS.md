# VapeCache API Benchmarks

Benchmark suite for the VapeCache public API using BenchmarkDotNet.

## Benchmark Classes

- `CacheServiceApiBenchmarks`
- `TypedCollectionsBenchmarks`
- `CircuitBreakerPerformanceBenchmarks`
- `RedisClientHeadToHeadBenchmarks`
- `RedisThroughputHeadToHeadBenchmarks`
- `RedisEndToEndHeadToHeadBenchmarks`
- `RedisModuleHeadToHeadBenchmarks`

## Run Commands

### Single suite

```powershell
dotnet run -c Release --project VapeCache.Benchmarks -- --filter "*RedisClientHeadToHeadBenchmarks*"
dotnet run -c Release --project VapeCache.Benchmarks/VapeCache.Benchmarks.Runner.csproj -- compare client --job Short
```

### Organized suite runner

```powershell
dotnet run -c Release --project VapeCache.Benchmarks/VapeCache.Benchmarks.Runner.csproj -- list-suites
dotnet run -c Release --project VapeCache.Benchmarks/VapeCache.Benchmarks.Runner.csproj -- featuresets cache --job Short
dotnet run -c Release --project VapeCache.Benchmarks/VapeCache.Benchmarks.Runner.csproj -- compare hotpath --job Short
dotnet run -c Release --project VapeCache.Benchmarks/VapeCache.Benchmarks.Runner.csproj -- compare all --job Short
```

### All head-to-head suites (recommended)

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Job Short -Mode fair
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Job Short -Mode realworld
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Job Medium -Mode fair -Profile aggressive
```

### Throughput suite (concurrency/pipeline matrix)

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Suite throughput -Quick -Mode fair
```

Optional env overrides for throughput matrix:

- `VAPECACHE_BENCH_THROUGHPUT_OPERATIONS`
- `VAPECACHE_BENCH_THROUGHPUT_PAYLOADS`
- `VAPECACHE_BENCH_THROUGHPUT_CONCURRENCY`
- `VAPECACHE_BENCH_THROUGHPUT_PIPELINE_DEPTH`
- `VAPECACHE_BENCH_THROUGHPUT_TOTAL_OPS`
- `VAPECACHE_BENCH_THROUGHPUT_CONNECTIONS`
- `VAPECACHE_BENCH_THROUGHPUT_DEDICATED_WORKERS`
- `VAPECACHE_BENCH_THROUGHPUT_MAX_INFLIGHT`

### Payload scaling pass

```powershell
$env:VAPECACHE_BENCH_CLIENT_OPERATIONS = "StringSetGet"
$env:VAPECACHE_BENCH_CLIENT_PAYLOADS = "1024,4096,16384"
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Suite client -Job Short -Mode fair
```

### Allocation fairness matrix (instrumentation + read path)

`RedisClientHeadToHeadBenchmarks` and `RedisEndToEndHeadToHeadBenchmarks` now include:

- `EnableInstrumentation`: `false|true`
- `ReadPath`: `Lease|Materialized` (VapeCache side)

Use `EnableInstrumentation=false` for client-core comparisons and run `true` separately to quantify observability tax.

### Contention matrix (thread/scheduler sensitivity)

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Suite client -Job Short -Mode fair -ContentionMatrix -ContentionProcessorCounts "4,16,32"
```

## Reporting Audiences

- **Hot-path comparison claims:** `compare hotpath` (or `compare client|throughput|endtoend`)
- **Feature/fallback behavior claims:** `featuresets cache`
- **Extended parity behavior:** `compare modules|datatypes`
- **Mixed coverage:** `compare all`

## Enterprise Interpretation Rules

1. Use medians from repeated runs (N>=3).
2. Evaluate throughput, allocations, and tail latency together.
3. Validate sustained runs (minutes, not seconds) before publishing claims.
4. Keep environment fixed across A/B variants.

## Key Artifact

`comparison.md` is generated per run and summarizes:

- scenario
- SER vs Vape means
- ratio (Vape/SER)
- winner
- allocation stats
- reporting audience (`VAPECACHE_BENCH_REPORT_AUDIENCE`)

Look under `BenchmarkDotNet.Artifacts/`.

## Config

`EnterpriseBenchmarkConfig` (`VapeCache.Benchmarks/EnterpriseBenchmarkConfig.cs`) currently provides:

- net10 jobs
- memory diagnoser
- p50/p90/p95 percentile columns
- markdown/html/csv/json exporters
- compact console logger for signal-focused output
- optional env-tuned run controls:
  - `VAPECACHE_BENCH_LAUNCH_COUNT`
  - `VAPECACHE_BENCH_WARMUP_COUNT`
  - `VAPECACHE_BENCH_ITERATION_COUNT`

## BenchmarkDotNet Official Docs

- https://benchmarkdotnet.org/
- https://benchmarkdotnet.org/articles/guides/getting-started.html
- https://benchmarkdotnet.org/articles/configs/configs.html
- https://benchmarkdotnet.org/articles/configs/jobs.html
- https://benchmarkdotnet.org/articles/configs/diagnosers.html
- https://benchmarkdotnet.org/articles/guides/console-args.html
- https://github.com/dotnet/BenchmarkDotNet/releases
