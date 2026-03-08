# VapeCache Phase 1 Benchmark Report

## Snapshot

- **Date:** March 4, 2026
- **Build:** `Release` (`.NET 10.0`)
- **Target:** `VapeCache` vs `StackExchange.Redis` (SER)
- **Redis:** `192.168.100.50:6379` (ACL auth)
- **Harness:** strict 10-trial runs, host isolation enabled, Server GC enabled
- **Workload focus:** Grocery Store comparison (`50,000 shoppers`, `40 max cart items`)
- **Authoritative artifact:** `docs/GROCERY_HEAD_TO_HEAD_2026-03-04.md`

## Configuration Used

- `MaxDegree=72` (stability-first default)
- `MuxProfile=FullTilt`
- `MuxConnections=12`
- `MuxInFlight=8192`
- `CleanupRunKeys=true`
- `Track=both` with **isolated track execution** (apples and optimized run separately per trial)

## Headline Result

**VapeCache is ahead of SER on the optimized hot path, but behind on apples/parity in this authoritative strict run.**

## Strict Results (10 Trials, 50k/40)

| View | Vape Median | SER Median | Median Ratio (Vape/SER) | Ratio Spread | Ratio CoV |
|---|---:|---:|---:|---:|---:|
| Both (isolated) overall | 26,795 | 25,586 | **1.047** | 0.879 .. 1.121 | 7.1% |
| OptimizedProductPath (from isolated both) | 26,795 | 25,586 | **1.040** | 0.879 .. 1.121 | 7.1% |
| ApplesToApples (from isolated both) | 23,808 | 25,605 | **0.920** | 0.767 .. 0.990 | 7.6% |

## Interpretation

- **Hot path claim:** supported (`OptimizedProductPath` median ratio > `1.00`).
- **Parity path claim:** not supported in this run (`ApplesToApples` median ratio < `1.00`).
- **Measurement quality:** isolated-track execution plus host isolation kept ratio CoV under 8% for both tracks.

## Reproduction Commands

```powershell
# strict both-track report (isolated apples + optimized per trial)
powershell -ExecutionPolicy Bypass -File tools/run-grocery-head-to-head.ps1 `
  -Trials 10 `
  -Track both `
  -ShopperCount 50000 `
  -MaxCartSize 40 `
  -MaxDegree 72 `
  -MuxProfile FullTilt `
  -MuxConnections 12 `
  -MuxInFlight 8192 `
  -RequireHostIsolation `
  -MaxHostCpuPercent 40 `
  -StableCpuSamples 6 `
  -RedisHost 192.168.100.50 `
  -RedisUsername admin `
  -RedisPassword "redis4me!!"
```

```powershell
# authoritative report path
Get-Content BenchmarkDotNet.Artifacts/grocery-head-to-head/latest/comparison.md
```

## Notes

- `Track=both` now runs in isolated mode by default to avoid apples/optimized coupling in one process.
- Run-key cleanup uses `UNLINK` (with fallback to `DEL`) to reduce cleanup stalls between trials.
- Connection-string parsing now supports both URI style (`redis://...`) and StackExchange style (`host:port,user=...,password=...`).
- Messaging split should stay explicit:
  - `OptimizedProductPath` = hot-path throughput claim.
  - `ApplesToApples` = parity/fallback behavior claim.
