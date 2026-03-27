# Benchmark Results (2026-03-20, America/Chicago)

This snapshot captures strict/fair and tuned grocery head-to-head runs after the lane-arbitration and harness cleanup pass.

- Runtime: .NET 10 / C# 14 codebase
- Harness: `tools/run-grocery-head-to-head.ps1`
- Trials per run class: `2`
- Shopper count: `50000`
- Redis endpoint: `benchmark-redis.example.internal:6379` (ACL auth; username `admin`, password redacted)

## Commands

Strict/Fair (track defaults disabled):

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-grocery-head-to-head.ps1 `
  -Trials 2 `
  -Track both `
  -ShopperCount 50000 `
  -SkipBuild `
  -DisableTrackDefaults `
  -MuxProfile LowLatency `
  -CleanupRunKeys true `
  -EnforceMetricGates `
  -MaxP50Ratio 1.25 `
  -MaxP95Ratio 1.30 `
  -MaxP99Ratio 1.35 `
  -MaxAllocRatio 1.40 `
  -MaxRunRetries 1 `
  -ChildRunTimeoutSeconds 1200 `
  -RedisHost benchmark-redis.example.internal `
  -RedisPort 6379 `
  -RedisUsername admin `
  -RedisPassword <redacted> `
  -SummaryJsonPath docs/benchmarks/grocery_strict_2026-03-20.json
```

Tuned/Showcase (track defaults enabled):

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-grocery-head-to-head.ps1 `
  -Trials 2 `
  -Track both `
  -ShopperCount 50000 `
  -SkipBuild `
  -MuxProfile FullTilt `
  -CleanupRunKeys true `
  -EnforceMetricGates `
  -MaxP50Ratio 1.25 `
  -MaxP95Ratio 1.30 `
  -MaxP99Ratio 1.35 `
  -MaxAllocRatio 1.40 `
  -MaxRunRetries 1 `
  -ChildRunTimeoutSeconds 1200 `
  -RedisHost benchmark-redis.example.internal `
  -RedisPort 6379 `
  -RedisUsername admin `
  -RedisPassword <redacted> `
  -SummaryJsonPath docs/benchmarks/grocery_tuned_2026-03-20.json
```

## Strict/Fair Summary

- Combined median throughput ratio (Vape/SER): `1.762`
- Median latency ratios: `p50=0.554`, `p95=0.628`, `p99=0.657`
- Median allocation ratio (Vape/SER): `0.413`
- Track geometric mean throughput ratio: `1.277`

Track split:

- ApplesToApples: throughput ratio median `0.925`, p50 ratio `1.060`, p95 ratio `1.227`, p99 ratio `1.038`, alloc ratio `NaN` (one run reported non-finite alloc ratio)
- OptimizedProductPath: throughput ratio median `1.762`, p50 ratio `0.554`, p95 ratio `0.628`, p99 ratio `0.657`, alloc ratio `0.413`

## Tuned/Showcase Summary

- Combined median throughput ratio (Vape/SER): `1.680`
- Median latency ratios: `p50=0.580`, `p95=0.621`, `p99=0.760`
- Median allocation ratio (Vape/SER): `0.399`
- Track geometric mean throughput ratio: `1.327`

Track split:

- ApplesToApples: throughput ratio median `1.046`, p50 ratio `0.940`, p95 ratio `1.108`, p99 ratio `0.929`, alloc ratio `1.277`
- OptimizedProductPath: throughput ratio median `1.684`, p50 ratio `0.580`, p95 ratio `0.621`, p99 ratio `0.760`, alloc ratio `0.399`

## Notes

- Tuned run printed noise warning (`ThroughputRatioCoV > 8%`) on combined ratio stability.
- OptimizedProductPath remains strongly faster than SER in both run classes.
- Strict/Fair apples path still needs parity work (throughput median below `1.0x` in this snapshot).
- Harness now logs child PIDs per track and enforces `-ChildRunTimeoutSeconds` to prevent stranded benchmark hosts.
- Summary JSON artifacts:
  - `docs/benchmarks/grocery_strict_2026-03-20.json`
  - `docs/benchmarks/grocery_tuned_2026-03-20.json`
