# Benchmark Results Snapshot

This page is the single "what happened last" checkpoint for head-to-head performance runs.

- Snapshot date: **2026-03-03**
- Artifact sources:
  - `artifacts/compare-both-50k-trials10-strict-20260303-093721.log`
  - `BenchmarkDotNet.Artifacts/head-to-head/20260225-234119/20260225-234121/comparison.md` (latest full BDN client suite snapshot)
- Host profile used for this run:
  - CPU: Intel i7-14700K
  - RAM: 64 GB
  - Runtime: .NET 10 (`Release`)
  - Redis target: `192.168.100.50:6379` (ACL auth for strict GroceryStore trials)

## Latest Summary

- Strict GroceryStore `both` track run (50k shoppers, 10 trials):
  - `OptimizedProductPath`: median ratio of medians `0.968` vs SER, ratio spread `0.891..1.009`, ratio CoV `3.6%`
  - `ApplesToApples`: median ratio of medians `0.868` vs SER, ratio spread `0.782..0.916`, ratio CoV `4.2%`
- Reporting split:
  - Hot-path claims: `OptimizedProductPath`
  - Feature/parity/fallback claims: `ApplesToApples`
- Recommended production knobs for this workload:
  - `MuxProfile=FullTilt`
  - `MuxConnections=16`
  - `MuxInFlight=8192`
  - `MuxCoalesce=true`
  - `MaxDegree=64`
  - `DOTNET_GCServer=1`

## GroceryStore Strict Vs SER (2026-03-03, 10 Trials)

| Track | Trials | Vape Median (shoppers/sec) | SER Median (shoppers/sec) | Median Ratio | Ratio Of Medians | Ratio CoV | Ratio Spread |
|---|---:|---:|---:|---:|---:|---:|---|
| OptimizedProductPath | 10 | 24,532 | 25,351 | 0.980 | 0.968 | 3.6% | 0.891 .. 1.009 |
| ApplesToApples | 10 | 22,021 | 25,374 | 0.876 | 0.868 | 4.2% | 0.782 .. 0.916 |

## GroceryStore Historical Snapshot (2026-02-26)

Detailed artifact:
- `BenchmarkDotNet.Artifacts/docs/grocerystore-comparison-2026-02-26.md`

### 50k Shoppers (OptimizedProductPath)

| Metric | VapeCache | StackExchange.Redis | Delta | Winner |
|---|---:|---:|---:|---|
| Throughput (shoppers/sec) | 25,999.24 | 21,116.78 | +23.1% | VapeCache |
| p95 latency (ms) | 16.37 | 15.01 | -9.0% | StackExchange.Redis |
| p99 latency (ms) | 24.88 | 22.65 | -9.9% | StackExchange.Redis |
| p999 latency (ms) | 28.50 | 32.87 | +13.3% lower | VapeCache |
| Alloc bytes/shopper | 20,920 | 33,059 | +36.7% lower | VapeCache |

### 100k Shoppers (OptimizedProductPath)

| Metric | VapeCache | StackExchange.Redis | Delta | Winner |
|---|---:|---:|---:|---|
| Throughput (shoppers/sec) | 27,740.95 | 27,404.16 | +1.2% | VapeCache |
| p95 latency (ms) | 15.56 | 13.90 | -12.0% | StackExchange.Redis |
| p99 latency (ms) | 24.63 | 17.65 | -39.5% | StackExchange.Redis |
| p999 latency (ms) | 29.62 | 31.71 | +6.6% lower | VapeCache |
| Alloc bytes/shopper | 21,005 | 32,916 | +36.2% lower | VapeCache |

## Latest Results (Client StringSet/Get, 256B payload)

| Scenario | SER Mean (us) | Vape Mean (us) | Ratio (Vape/SER) | Delta % | Winner | SER Alloc B/op | Vape Alloc B/op |
|---|---:|---:|---:|---:|---|---:|---:|
| Instrumentation=false, ReadPath=Lease | 207.10 | 179.56 | 0.867 | -13.3% | VapeCache | 1056 | 5255 |
| Instrumentation=false, ReadPath=Materialized | 194.49 | 181.06 | 0.931 | -6.9% | VapeCache | 1056 | 5491 |
| Instrumentation=true, ReadPath=Lease | 217.66 | 183.06 | 0.841 | -15.9% | VapeCache | 1056 | 5252 |
| Instrumentation=true, ReadPath=Materialized | 212.42 | 199.62 | 0.940 | -6.0% | VapeCache | 1056 | 5496 |

## Read This Correctly

- Throughput/mean is winning in this snapshot.
- Allocation footprint is still higher than SER here; do not ignore this for sustained-load p99/p999.
- Treat this page as a checkpoint, not a permanent claim. Re-run and update for every release candidate.

## Reproduce Quickly

```powershell
$env:VAPECACHE_REDIS_CONNECTIONSTRING = "redis://localhost:6379/0"
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-benchmarks.ps1 -Job Short -Mode fair
```
