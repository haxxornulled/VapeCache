# Benchmark Results Snapshot

This is the single "latest known benchmark checkpoint" for grocery head-to-head runs.

## Read First

- This is a snapshot, not a universal claim.
- Always label result class:
  - **Strict/Fair (authoritative):** parity-oriented settings.
  - **Tuned/Showcase (engineering):** workload-tuned settings.
- Do not publish "faster than SER" claims without date, workload, and class.
- Policy: [BENCHMARK_CLAIMS_POLICY.md](BENCHMARK_CLAIMS_POLICY.md)

## Current Snapshot (2026-03-20, America/Chicago)

- Commit: `2fc7e49`
- Redis endpoint class:
  - Hardened Debian direct endpoint (`benchmark-redis.example.internal:6379`)
  - ACL auth (`admin`, password redacted)
- Harness:
  - `tools/run-grocery-head-to-head.ps1`
  - Child PID logging + timeout cleanup enabled (`-ChildRunTimeoutSeconds`)
- Workload:
  - `50000` shoppers
  - `2` trials per run class

Detailed report:
- [BENCHMARK_RESULTS_2026-03-20.md](BENCHMARK_RESULTS_2026-03-20.md)

## Latest Summary

- Strict/Fair (`-DisableTrackDefaults`, `LowLatency`):
  - `OptimizedProductPath`: throughput ratio median `1.762`, p99 ratio median `0.657`, alloc ratio median `0.413`
  - `ApplesToApples`: throughput ratio median `0.925`, p99 ratio median `1.038` (needs parity tuning)
  - Track geometric mean throughput ratio: `1.277`
- Tuned/Showcase (track defaults enabled, `FullTilt`):
  - `OptimizedProductPath`: throughput ratio median `1.684`, p99 ratio median `0.760`, alloc ratio median `0.399`
  - `ApplesToApples`: throughput ratio median `1.046`, p99 ratio median `0.929`
  - Track geometric mean throughput ratio: `1.327`

## Historical Reports

- [BENCHMARK_RESULTS_2026-03-19.md](BENCHMARK_RESULTS_2026-03-19.md)
- [BENCHMARK_RESULTS_2026-03-20.md](BENCHMARK_RESULTS_2026-03-20.md)
