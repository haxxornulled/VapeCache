# Benchmarking Status

Current benchmark posture is production-grade and actively used for regression detection.

## What Exists

- Head-to-head suites in `VapeCache.Benchmarks` for:
  - Client ops (`RedisClientHeadToHeadBenchmarks`)
  - End-to-end ops (`RedisEndToEndHeadToHeadBenchmarks`)
  - Module ops (`RedisModuleHeadToHeadBenchmarks`)
- Enterprise config in `VapeCache.Benchmarks/EnterpriseBenchmarkConfig.cs`
- Automation scripts:
  - `tools/run-head-to-head-benchmarks.ps1`
  - `tools/run-head-to-head-with-capture.ps1`
  - `tools/run-grocery-head-to-head.ps1`

## How We Judge Wins

A performance claim is accepted only when:

1. Same host + same Redis target + same run mode.
2. At least 3 repeated runs.
3. Median Vape/SER ratio is favorable.
4. Allocation and GC behavior are not regressing.
5. Tail latency checks (p95/p99/p999) are acceptable under sustained load.

## Current Focus Areas

- Bigger payload scaling (1KB/4KB/16KB).
- Allocation pressure reduction on hot paths.
- Sustained load stability and tail latency.
- Reproducible evidence packs for release notes.

## Required Reading

Before modifying benchmark methodology, read:

- `docs/BENCHMARKING.md`
- BenchmarkDotNet official docs listed there
- `docs/ENGINEERING_PLAYBOOK.md`

## Known Gaps

- Some documentation examples historically emphasized means more than tail metrics.
- CI gating still needs stronger percentile/long-run enforcement in all pipelines.

These are documentation/process gaps, not missing benchmark infrastructure.
