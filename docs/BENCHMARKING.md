# VapeCache Benchmarking Guide

This guide defines how to run and interpret VapeCache benchmarks with an enterprise-grade workflow.

## Scope

Use this guide for:
- Head-to-head Redis client comparisons (VapeCache vs StackExchange.Redis)
- End-to-end workload comparisons
- Module command comparisons (JSON/FT/BF/TS)
- Sustained-load and tail-latency verification

Use `docs/ENGINEERING_PLAYBOOK.md` for profiler/Wireshark workflow.

## Ground Rules (Enterprise)

- Run in `Release` only.
- Use dedicated benchmark hosts when possible (no IDE/debugger attached).
- Keep Redis target, host CPU governor, and network path fixed across A/B runs.
- Never claim a win from a single run; use repeated runs and median reporting.
- Evaluate throughput + allocations + tail latency together.

## Claim Classes (Required)

Use explicit report classes for every benchmark publication:

- **Strict/Fair (authoritative):** same knobs across tracks/providers.
- **Tuned/Showcase (engineering):** workload-specific tuning.

For grocery comparisons, `-DisableTrackDefaults` enforces strict/fair mode.

Policy reference: [BENCHMARK_CLAIMS_POLICY.md](BENCHMARK_CLAIMS_POLICY.md)

## Quick Start

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Job Short -Mode fair
```

High-pressure workstation profile (longer/more stable runs):

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Job Medium -Mode fair -Profile aggressive
```

`fair` mode disables instrumentation overhead for apples-to-apples client benchmarking.

Strict/fair baseline defaults (when `-DisableTrackDefaults` is used and no explicit overrides are passed):
- `MaxDegree=6`
- `MuxConnections=1`
- `MuxAdaptiveCoalescing=false`
- `CleanupRunKeys=false`

Remote endpoint auth requirement:
- Non-local Redis targets now require ACL authentication.
- For `tools/run-grocery-head-to-head.ps1`, provide both `-RedisUsername` and `-RedisPassword` when `-RedisHost` is not local.
- For `tools/run-head-to-head-benchmarks.ps1`, set `VAPECACHE_REDIS_USERNAME` and `VAPECACHE_REDIS_PASSWORD` (or a credentialed `VAPECACHE_REDIS_CONNECTIONSTRING`) for non-local targets.

Strict/fair grocery report command:

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-grocery-head-to-head.ps1 `
  -Trials 5 `
  -Track both `
  -DisableTrackDefaults `
  -CleanupRunKeys false `
  -ShopperCount 50000 `
  -MaxCartSize 40 `
  -RedisHost benchmark-redis.example.internal `
  -RedisPort 6379 `
  -RedisUsername admin `
  -RedisPassword <redacted> `
  -FailBelowRatio 1.0
```

Spill-aware autoscaler tuning knobs for grocery runs (`tools/run-grocery-head-to-head.ps1`):
- `-MuxEnableSpillPressureSignals true|false`
- `-MuxSpillFilesThreshold <int>`
- `-MuxSpillActiveShardsThreshold <int>`
- `-MuxSpillImbalanceRatioThreshold <double>`
- `-MuxSpillSustainedWindowSeconds <int>`
- `-EnableDiskSpill true|false`
- `-SpillThresholdBytes <int>`
- `-SpillDirectory <path>`
- `-SpillPrimeRecords <int>`
- `-SpillPrimePayloadBytes <int>`

Hybrid hot-path guardrail knobs:
- `-HybridFastPath true|false`
- `-HybridAdmissionGate true|false`
- `-HybridAdmissionLimit <int>`
- `-HybridAdmissionWaitMs <int>`
- `-HybridMirrorWrites true|false`
- `-HybridWarmReadFallback true|false`
- `-HybridRemoveStaleFallbackOnMiss true|false`

Healthy Redis hybrid validation policy:
- When benchmarking hybrid healthy-path performance, keep `HybridFastPath=true`.
- Disable fallback warm/mirror work unless you are explicitly validating failover continuity.
- Keep command instrumentation off for fair client/runtime comparisons. This pass found that always-on per-command timing materially distorts hybrid results.

Per-shopper command-matrix requirement:
- Grocery command coverage is not a partial smoke test. Each shopper must exercise Strings, Hashes, Lists, Sets, Sorted Sets, tag invalidation, and any capability-gated modules/types available on the target Redis instance.
- On Redis 8.6+ targets, each shopper must also execute the stream idempotence path (`XADD` with `IDMP`/`IDMPAUTO` semantics plus `XCFGSET`).
- When a target does not expose RedisJSON/RediSearch/RedisBloom/RedisTimeSeries, those commands should be reported as optional skips rather than silently removed from the benchmark story.

Current optimized hybrid validation snapshot (`2026-03-21`):
- Target: remote Redis `benchmark-redis.example.internal:6379` with ACL auth, optimized track, `ShopperCount=1000`, `MaxCartSize=35`.
- Median-of-3 result after the command stopwatch removal pass: throughput ratio `1.002`, allocation ratio `1.371`, latency ratios `p50=0.981`, `p95=1.006`, `p99=1.037`.
- Artifact: `artifacts/benchmarks/hybrid-optimized-no-command-stopwatch-3trial.json`
- Caveat: the harness still flagged the environment as noisy (`CoV > 8%`), so use an isolated Redis target and a quieter client host before publishing a stronger external claim.

Track defaults (when `-DisableTrackDefaults` is not used):
- `apples`: conservative spill pressure thresholds to preserve parity behavior.
- `optimized`: faster spill pressure response (`files=4000`, `shards=48`, `imbalance=1.75`, `window=20s`).

## Modes

- `fair`: instrumentation off, minimal observer noise.
- `realworld`: instrumentation on, closer to production telemetry overhead.

Run both for a complete story.

## Recommended Run Matrix

1. Payload scaling: `256`, `1024`, `4096`, `16384`.
2. Data types: String, Hash, List, module commands.
3. Modes: `fair` and `realworld`.
4. Repetitions: at least 3 full runs, compare medians.
5. Sustained pass: 5-15 minute load using console/store scripts.

## Standard Commands

### Head-to-head suites

```powershell
dotnet run -c Release --project VapeCache.Benchmarks/VapeCache.Benchmarks.Runner.csproj -- list-suites compare
dotnet run -c Release --project VapeCache.Benchmarks/VapeCache.Benchmarks.Runner.csproj -- compare hotpath --job Short
dotnet run -c Release --project VapeCache.Benchmarks/VapeCache.Benchmarks.Runner.csproj -- compare all --job Short
dotnet run -c Release --project VapeCache.Benchmarks/VapeCache.Benchmarks.Runner.csproj -- compare client --job Short
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Suite hotpath -Job Short -Mode fair
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Job Short -Mode fair
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Job Short -Mode realworld
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Job Medium -Mode fair -Profile aggressive
```

### Focus one suite

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Suite hotpath -Job Short -Mode fair
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Suite client -Job Short -Mode fair
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Suite endtoend -Job Short -Mode fair
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Suite modules -Job Short -Mode fair
```

## Audience-Labeled Reporting

- Hot-path comparison claims: run `compare hotpath` (or `-Suite hotpath`).
- Feature/fallback claims: run `featuresets cache`.
- Keep these report streams separate in status docs and PR summaries.

See [HOT_PATH_BENCHMARK_CHECKLIST.md](HOT_PATH_BENCHMARK_CHECKLIST.md) for the full tuning and gating checklist.

### Bigger payload pass (example)

```powershell
$env:VAPECACHE_BENCH_CLIENT_OPERATIONS = "StringSetGet"
$env:VAPECACHE_BENCH_CLIENT_PAYLOADS = "1024,4096,16384"
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Suite client -Job Short -Mode fair
```

## Output Artifacts

- BenchmarkDotNet summary tables (console and exports)
- `comparison.md` (winner/ratio summary)
- CSV/JSON/HTML/OpenMetrics exports via `EnterpriseBenchmarkConfig`

Primary artifact path:
- `BenchmarkDotNet.Artifacts/`

## Interpreting Results

- Throughput ratios (`Vape ops/sec / SER ops/sec`): `> 1.00` means VapeCache is faster.
- Time/latency ratios (`Vape time / SER time`): `< 1.00` means VapeCache is faster.
- Allocation deltas matter even when mean latency wins.
- Confirm p95/p99/p999 behavior under sustained load before publishing claims.

## CI Gate Recommendation

Use median-of-N gating, not single-run gating:

1. Run scenario N=3 (or 5).
2. Compute median throughput and median ratio.
3. Fail if median ratio exceeds threshold (for example `> 1.00`).
4. Track allocation and GC trends separately.

## BenchmarkDotNet Usage Notes

The repo uses:
- `BenchmarkDotNet` package in `VapeCache.Benchmarks/VapeCache.Benchmarks.csproj`
- `VapeCache.Benchmarks.Runner.csproj` as the suite-oriented launcher
- `EnterpriseBenchmarkConfig` in `VapeCache.Benchmarks/EnterpriseBenchmarkConfig.cs`

`EnterpriseBenchmarkConfig` currently includes:
- net10 jobs
- memory diagnoser
- p50/p90/p95 percentile columns
- markdown/html/csv/json exporters
- compact results logger
- configurable launch/warmup/iteration counts via env:
  - `VAPECACHE_BENCH_LAUNCH_COUNT`
  - `VAPECACHE_BENCH_WARMUP_COUNT`
  - `VAPECACHE_BENCH_ITERATION_COUNT`
  - `VAPECACHE_BENCH_RUNTIME_MODE` (`both`, `wks`, or `svr`)

Runtime labels in benchmark outputs:
- `net10-wks` / `wks`: .NET 10 Workstation GC job (`Server=false`)
- `net10-svr` / `svr`: .NET 10 Server GC job (`Server=true`)

## Official BenchmarkDotNet References

Read these before changing benchmark methodology:

- Overview: https://benchmarkdotnet.org/
- Getting started: https://benchmarkdotnet.org/articles/guides/getting-started.html
- Configuration: https://benchmarkdotnet.org/articles/configs/configs.html
- Jobs: https://benchmarkdotnet.org/articles/configs/jobs.html
- Toolchains: https://benchmarkdotnet.org/articles/configs/toolchains.html
- Diagnosers: https://benchmarkdotnet.org/articles/configs/diagnosers.html
- Console args: https://benchmarkdotnet.org/articles/guides/console-args.html
- Project releases: https://github.com/dotnet/BenchmarkDotNet/releases

## Do Not Do This

- Do not compare Debug vs Release.
- Do not compare different Redis hosts between A/B variants.
- Do not publish means without allocation and percentile context.
- Do not present single-run outliers as enterprise conclusions.
