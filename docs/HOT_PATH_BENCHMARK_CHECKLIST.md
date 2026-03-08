# Hot Path Benchmark Checklist

Use this checklist to benchmark mux/connection tuning without mixing in non-hot-path feature suites.

## Audience Split (Required)

- Hot-path comparison claims:
  - `compare hotpath` (bundle of `client` + `throughput` + `endtoend`)
- Feature/fallback behavior claims:
  - `featuresets cache`

Do not use `featuresets` results to claim hot-path client throughput wins.

## Tuned Defaults Baseline

Start from these tuned defaults before any experiment:

- `TransportProfile=FullTilt`
- `EnableCoalescedSocketWrites=true`
- `EnableAdaptiveCoalescing=true`
- `CoalescedWriteMaxBytes=524288`
- `CoalescedWriteMaxSegments=192`
- `CoalescedWriteSmallCopyThresholdBytes=1536`
- `AdaptiveCoalescingLowDepth=6`
- `AdaptiveCoalescingHighDepth=56`
- `AdaptiveCoalescingMinWriteBytes=65536`
- `AdaptiveCoalescingMinSegments=48`
- `AdaptiveCoalescingMinSmallCopyThresholdBytes=384`
- `EnableSocketRespReader=false` (promote only after validation)
- `UseDedicatedLaneWorkers=false` (promote only under sustained thread-pool pressure)

## Execution Checklist

1. Lock environment
- Pin Redis target, network path, and host CPU state.
- Run `Release` only.

2. Run hot-path comparison suite
- Runner:
```powershell
dotnet run -c Release --project VapeCache.Benchmarks/VapeCache.Benchmarks.Runner.csproj -- compare hotpath --job Short
```
- Script:
```powershell
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Suite hotpath -Job Short -Mode fair
```

3. Run feature/fallback suite separately
```powershell
dotnet run -c Release --project VapeCache.Benchmarks/VapeCache.Benchmarks.Runner.csproj -- featuresets cache --job Short
```

4. Validate realistic overhead
```powershell
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Suite hotpath -Job Short -Mode realworld
```

5. Tune one knob group at a time
- Lane pressure group: `Connections`, `MaxInFlightPerConnection`
- Write shaping group: coalesced/adaptive sizing knobs
- Reader/worker group: `EnableSocketRespReader`, `UseDedicatedLaneWorkers`

6. Promote only on median-of-3 (or 5)
- Compare medians for throughput, allocation, and tail latency.
- Keep `VAPECACHE_BENCH_REPORT_AUDIENCE` aligned with the suite audience.

## Fast Tuning Playbook

1. Queue depth high, CPU available:
- Increase `Connections` first.
- Then increase `MaxInFlightPerConnection`.

2. Queue depth low, throughput limited:
- Increase `CoalescedWriteMaxBytes` / `CoalescedWriteMaxSegments` (or use `Custom` profile).

3. Tail latency spikes under burst:
- Reduce coalesced max bytes/segments.
- Keep adaptive coalescing enabled.

4. Reader-side parse bottleneck:
- Enable `EnableSocketRespReader` and rerun hotpath suite.

5. Thread-pool contention:
- Enable `UseDedicatedLaneWorkers` and rerun hotpath suite.

## Gate Criteria (Suggested)

- Throughput ratio: VapeCache/SER median ratio <= 1.00 for hotpath suite.
- Tail safety: no p99 regression beyond agreed SLO threshold.
- Stability: no timeout-rate increase under sustained runs.

## Related Docs

- [MUX_FAST_PATH_ARCHITECTURE.md](MUX_FAST_PATH_ARCHITECTURE.md)
- [BENCHMARKING.md](BENCHMARKING.md)
- [BENCHMARK_RESULTS.md](BENCHMARK_RESULTS.md)
