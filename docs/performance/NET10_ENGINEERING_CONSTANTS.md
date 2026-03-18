# .NET 10 Engineering Constants

This document turns our .NET 10 lessons into standing rules we default to unless we have measured reasons not to.

## Why this exists

Feature hype changes. Engineering constants should not.

These constants keep us aligned on performance, reliability, and maintainability across runtime, infrastructure, and benchmark work.

## Constants

1. Prefer modern crypto certificate lookup paths.
- Do not introduce new SHA-1-only thumbprint assumptions.
- Use stronger hash-algorithm lookup paths when available.

2. Use `DateOnly`/ISO-week APIs for business-calendar logic.
- Avoid ad hoc week math.
- Keep week/year/date conversions centralized and testable.

3. Treat span-based text processing as the default in hot paths.
- Avoid allocating normalization/intermediate strings in high-frequency code.
- Use stack/pooled buffers when bounded and safe.

4. Write abstraction-friendly code, then verify with benchmarks.
- Keep APIs clean (`IEnumerable<T>`, interfaces) and trust JIT improvements where applicable.
- Benchmark every hot-path claim instead of assuming wins.

5. Keep SDK/package graphs lean.
- Prefer framework-provided assets where possible.
- Remove redundant package references that add restore/deploy/security noise.

6. Make observability explicit, not incidental.
- Use `ActivitySource` and structured tags for critical flows.
- Record perf context (GC + queueing + throughput) during benchmark runs.

7. Use GC/thread-pool telemetry as first-class perf signals.
- Track allocation rate, collections, queue depth, and worker saturation in perf investigations.
- Never optimize blind.

8. Favor mechanical sympathy in runtime design.
- Queueing collapse beats theoretical throughput every time.
- Add admission/backpressure before saturation cliffs, not after.

9. Optimize healthy fast-paths without sacrificing failover safety.
- Keep fast-path branch costs low.
- Preserve breaker/fallback correctness under failure conditions.

10. Every perf change ships with proof.
- Build + tests + benchmark delta + variance notes are required for sign-off.

## VapeCache-specific application

- Hybrid executor: healthy fast-path and admission control are expected on hot operations.
- Grocery benchmark harness: spill diagnostics and spill priming support are maintained for spill-aware autoscaler validation.
- Benchmark reporting: always call out variance and environment noise.

## Review checklist

- Is the hot path allocation-aware?
- Is there an admission/backpressure story?
- Is there a reproducible benchmark command?
- Are diagnostics attached to the claim?
- Is failover behavior still correct?
