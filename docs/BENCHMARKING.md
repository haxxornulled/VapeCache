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

## Quick Start

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Job Short -Mode fair
```

High-pressure workstation profile (longer/more stable runs):

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Job Medium -Mode fair -Profile aggressive
```

`fair` mode disables instrumentation overhead for apples-to-apples client benchmarking.

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
dotnet run -c Release --project VapeCache.Benchmarks/VapeCache.Benchmarks.Runner.csproj -- compare all --job Short
dotnet run -c Release --project VapeCache.Benchmarks/VapeCache.Benchmarks.Runner.csproj -- compare client --job Short
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Job Short -Mode fair
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Job Short -Mode realworld
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Job Medium -Mode fair -Profile aggressive
```

### Focus one suite

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Suite client -Job Short -Mode fair
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Suite endtoend -Job Short -Mode fair
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Suite modules -Job Short -Mode fair
```

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

- `Ratio (Vape/SER) < 1.00` means VapeCache is faster.
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
