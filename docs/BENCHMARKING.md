# Benchmarking

This document describes the benchmark surface that currently exists in the OSS repository.

It intentionally reflects the slimmed-down benchmark harness now shipped in `VapeCache.Benchmarks`.

## Current Benchmark Scope

The active benchmark harness contains three benchmark classes:

- `RedisRespProtocolBenchmarks`
- `RespParserLiteBenchmarks`
- `RedisBackendRoundTripBenchmarks`

The first two are clone-safe microbenchmarks.
The third is a live benchmark that talks to Redis and/or KeyDB.

## Default Safety Contract

The default benchmark command must be safe on a clean machine:

```powershell
dotnet run -c Release --project .\VapeCache.Benchmarks\VapeCache.Benchmarks.csproj
```

That command runs only the `Micro` benchmark category by default.

Today that means:

- `RedisRespProtocolBenchmarks`
- `RespParserLiteBenchmarks`

This protects contributors from accidentally running live network benchmarks just because they cloned the repo.

## Running A Specific Microbenchmark

```powershell
dotnet run -c Release --project .\VapeCache.Benchmarks\VapeCache.Benchmarks.csproj -- --filter "*RedisRespProtocolBenchmarks*"
dotnet run -c Release --project .\VapeCache.Benchmarks\VapeCache.Benchmarks.csproj -- --filter "*RespParserLiteBenchmarks*"
```

## Running Live Backends

Live backend runs are opt-in.

### Start local containers

```powershell
docker compose -f .\infra\docker-compose.benchmarks.yml up -d
```

### Run both Redis and KeyDB

```powershell
pwsh .\tools\run-live-benchmarks.ps1 -EnsureContainers
```

### Run a single backend

```powershell
pwsh .\tools\run-live-benchmarks.ps1 -Backend redis
pwsh .\tools\run-live-benchmarks.ps1 -Backend keydb
```

### Manual live opt-in

```powershell
dotnet run -c Release --project .\VapeCache.Benchmarks\VapeCache.Benchmarks.csproj -- --include-live --filter "*RedisBackendRoundTripBenchmarks*"
```

You can also use:

- env var: `VAPECACHE_BENCH_INCLUDE_LIVE=true`

## Benchmark Profiles

`tools/run-live-benchmarks.ps1` supports:

- `-Mode smoke`
- `-Mode full`

Current profile behavior:

- `smoke`: minimal payload set for fast sanity checks
- `full`: broader payload coverage for deeper comparison

## Artifact Location

By default BenchmarkDotNet writes to:

- `BenchmarkDotNet.Artifacts/`

`tools/run-live-benchmarks.ps1` also exports a live backend comparison report when both backends are run.

## Contributor Rules

### If you add a benchmark

You must decide whether it is:

- `Micro`
- `Live`

If the benchmark depends on Redis, Docker, network access, or external processes, it is `Live`.

### If you change default benchmark behavior

You must preserve the clone-safe default path unless the repo explicitly changes that policy in docs and scripts together.

### If you publish benchmark claims

Use:

- [BENCHMARK_CLAIMS_POLICY.md](BENCHMARK_CLAIMS_POLICY.md)

Do not publish single-run marketing claims from a local workstation without documenting:

- benchmark class
- backend
- payload range
- build configuration
- whether the run was micro or live

## Historical Note

Older docs in the repo may reference:

- `VapeCache.Benchmarks.Runner.csproj`
- suite catalogs
- grocery head-to-head scripts
- legacy comparison harnesses

Those surfaces are no longer part of the active OSS benchmark harness.
Treat them as historical references only unless and until they are deliberately restored.
