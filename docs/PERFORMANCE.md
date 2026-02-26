# VapeCache Performance

Performance methodology and interpretation for enterprise evaluation.

## Summary

VapeCache is designed to outperform general-purpose Redis clients on caching-heavy paths while preserving resilience and observability.

Do not treat any single benchmark run as a release claim. Use repeated runs, medians, and tail-latency checks.

## Enterprise Methodology

All performance statements should follow this baseline:

- Release builds only
- Same host + same Redis target for A/B
- Run `fair` mode and `realworld` mode
- Repeat each scenario at least 3 times
- Judge by median ratio and stability
- Review throughput, allocations, and p95/p99/p999 together

## Standard Benchmark Commands

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Job Short -Mode fair
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Job Short -Mode realworld
```

Single suite examples:

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Suite client -Job Short -Mode fair
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Suite endtoend -Job Short -Mode fair
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Suite modules -Job Short -Mode fair
```

Payload scaling example:

```powershell
$env:VAPECACHE_BENCH_CLIENT_OPERATIONS = "StringSetGet"
$env:VAPECACHE_BENCH_CLIENT_PAYLOADS = "1024,4096,16384"
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Suite client -Job Short -Mode fair
```

Instrumentation and materialization matrix:

- Run `EnableInstrumentation=false` and `EnableInstrumentation=true` separately.
- Run Vape read paths in both `Lease` and `Materialized` modes.
- Compare like-for-like before drawing performance conclusions.

## Interpreting Results

- `Ratio (Vape/SER) < 1.00` means VapeCache is faster.
- Mean wins are not enough by themselves.
- Allocation increases can hurt sustained-load tail latency.
- Tail latency (p95/p99/p999) under sustained load is mandatory for enterprise sign-off.

## Sustained Load and Tail Latency

For production confidence:

1. Run 5-15 minute sustained tests.
2. Capture p95/p99/p999 and GC behavior.
3. Compare both `fair` and `realworld` runs.
4. Confirm no unacceptable regressions in allocation pressure.

## Thread-Local Cache Behavior

`RedisMultiplexedConnection` uses a two-level cache for header buffers and payload-array scratch space:

- Thread-local (`[ThreadStatic]`) slot first for lock-free hot path reuse.
- Shared `ConcurrentBag` fallback for cross-thread returns.

This improves throughput, but bursty thread scheduling can shift retained memory between thread-local and shared caches. Always validate under multiple concurrency levels (low, medium, high contention), not just one fixed thread profile.

## Capture and Diagnostics

For packetization and transport-level evidence:

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-with-capture.ps1 -Job Short -ConnectionString "redis://localhost:6379/0" -Interface 1 -RedisPort 6379
```

Use `docs/ENGINEERING_PLAYBOOK.md` for analyzer, profiler, and packet capture workflow.

## Reporting Rules

When publishing results internally or externally:

- Include environment details (hardware, runtime, Redis version, mode).
- Include run count and median calculation.
- Include allocation and percentile context.
- Include artifact references (`comparison.md`, CSV/JSON/HTML exports).

## References

- [docs/BENCHMARKING.md](BENCHMARKING.md)
- [docs/ENGINEERING_PLAYBOOK.md](ENGINEERING_PLAYBOOK.md)
- [BenchmarkDotNet Official Docs](https://benchmarkdotnet.org/)
