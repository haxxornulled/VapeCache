# Hot Path Benchmark Checklist

This checklist now applies to the active `VapeCache.Benchmarks` harness and transport-focused validation work.

## Use This With

- [BENCHMARKING.md](BENCHMARKING.md)
- [TRANSPORT_MUX_AUTOSCALER_DEEP_DIVE.md](TRANSPORT_MUX_AUTOSCALER_DEEP_DIVE.md)
- [MUX_MAINTAINER_GUIDE.md](MUX_MAINTAINER_GUIDE.md)

## Checklist

1. Build `Release` only.
2. Run deterministic tests first when changing transport, mux, pooling, or timeout behavior.
3. Run the default benchmark harness and confirm only microbenchmarks are selected unless you explicitly opted into live runs.
4. If the change touches socket I/O, coalescing, sequencing, or resets, run a live backend benchmark as well.
5. Compare throughput, allocations, and tail latency together. Never optimize one in isolation.
6. If performance improves but resets, mismatches, orphaned responses, or pool churn worsen, treat the change as unsafe.
7. Update maintainer docs when invariants or operator guidance change.

## Historical Note

Older versions of this file referenced the removed runner project and deleted head-to-head scripts.
Those instructions are no longer current OSS guidance.
