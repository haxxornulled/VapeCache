# VapeCache Phase 1 Benchmark Report

## Snapshot

- **Date:** March 4, 2026
- **Build:** `Release` (`.NET 10.0`)
- **Target:** `VapeCache` vs `StackExchange.Redis` (SER)
- **Redis:** `192.168.100.50:6379` (ACL auth)
- **Harness:** strict 10-trial runs, host isolation enabled, Server GC enabled
- **Workload focus:** Grocery Store comparison (`50,000 shoppers`, `40 max cart items`)

## Configuration Used

- `MaxDegree=72` (stability-first default)
- `MuxProfile=FullTilt`
- `MuxConnections=8`
- `MuxInFlight=8192`
- `CleanupRunKeys=true`
- `Track=both` with **isolated track execution** (apples and optimized run separately per trial)

## Headline Result

**VapeCache is ahead of SER on the optimized path with stable strict-run medians.**

## Strict Results (10 Trials, 50k/40)

| View | Vape Median | SER Median | Median Ratio (Vape/SER) | Ratio Spread | Ratio CoV |
|---|---:|---:|---:|---:|---:|
| Both (isolated) overall | 22,524 | 18,918 | **1.191** | 1.085 .. 1.278 | 4.6% |
| OptimizedProductPath (from isolated both) | 22,524 | 18,918 | **1.17** | 1.085 .. 1.278 | 4.7% |
| ApplesToApples (from isolated both) | 19,045 | 18,706 | **1.01** | 0.902 .. 1.152 | 8.1% |
| Optimized-only run | 22,105 | 19,288 | **1.146** | 1.038 .. 1.260 | 5.8% |
| Apples-only run | 19,470 | 19,305 | **1.009** | 0.941 .. 1.119 | 5.2% |

## Interpretation

- **Hot path claim:** supported (`OptimizedProductPath` clearly > `1.00` median, with low spread/CoV in strict isolated runs).
- **Parity path claim:** near parity/slight lead on median; occasional under-runs still appear on apples.
- **Measurement quality:** isolated-track execution materially reduced cross-track interference seen in legacy combined mode.

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
  -MuxConnections 8 `
  -MuxInFlight 8192 `
  -RequireHostIsolation `
  -MaxHostCpuPercent 35 `
  -StableCpuSamples 8 `
  -RedisHost 192.168.100.50 `
  -RedisUsername admin `
  -RedisPassword "redis4me!!"
```

```powershell
# optimized-only strict confirmation
powershell -ExecutionPolicy Bypass -File tools/run-grocery-head-to-head.ps1 `
  -Trials 10 `
  -Track optimized `
  -ShopperCount 50000 `
  -MaxCartSize 40 `
  -MaxDegree 72 `
  -MuxProfile FullTilt `
  -MuxConnections 8 `
  -MuxInFlight 8192 `
  -RequireHostIsolation `
  -MaxHostCpuPercent 35 `
  -StableCpuSamples 8 `
  -RedisHost 192.168.100.50 `
  -RedisUsername admin `
  -RedisPassword "redis4me!!"
```

## Notes

- `Track=both` now runs in isolated mode by default to avoid apples/optimized coupling in one process.
- Run-key cleanup uses `UNLINK` (with fallback to `DEL`) to reduce cleanup stalls between trials.
- Connection-string parsing now supports both URI style (`redis://...`) and StackExchange style (`host:port,user=...,password=...`).

