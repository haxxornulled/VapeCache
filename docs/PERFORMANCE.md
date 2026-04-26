# Performance Guidance

This repository no longer ships the old head-to-head benchmark runner or grocery comparison harness.

Use these current documents instead:

- [BENCHMARKING.md](BENCHMARKING.md)
- [BENCHMARK_CLAIMS_POLICY.md](BENCHMARK_CLAIMS_POLICY.md)
- [TRANSPORT_MUX_AUTOSCALER_DEEP_DIVE.md](TRANSPORT_MUX_AUTOSCALER_DEEP_DIVE.md)
- [MUX_MAINTAINER_GUIDE.md](MUX_MAINTAINER_GUIDE.md)
- [../VapeCache.Tests/Integration/README.md](../VapeCache.Tests/Integration/README.md)

## Current Rule

Performance work in this repo should be split into two categories:

- deterministic correctness and invariants in tests
- micro or live performance validation in `VapeCache.Benchmarks`
- operator-style runtime stress in `VapeCache.Tests\Integration`

Do not rely on deleted scripts such as `tools/run-head-to-head-benchmarks.ps1` or the removed benchmark runner project.
Use `tools/run-runtime-stress.ps1` for concurrency/failover pressure against the current hybrid runtime.

## Maintainer Reminder

If you restore broader performance harnesses in the future, restore their code, tests, and docs together. Do not reintroduce command examples without a working surface behind them.
