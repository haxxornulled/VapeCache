# Spill Benchmark Results (March 17, 2026)

This report summarizes the spill engine matrix run after secure-path CRC optimization.

## Run Profile

- Command surface: `tools/run-spill-benchmarks.ps1`
- Job: `Short`
- Matrix:
  - `PayloadBytes`: `4096,65536,262144`
  - `WorkingSet`: `1024`
  - `SegmentSizeMegabytes`: `128`
  - `ValidateCrc`: `false,true`
  - `RuntimeMode`: `both`
- BDN controls:
  - `VAPECACHE_BENCH_LAUNCH_COUNT=1`
  - `VAPECACHE_BENCH_WARMUP_COUNT=2`
  - `VAPECACHE_BENCH_ITERATION_COUNT=6`

Runtime labels:
- `svr` (`net10-svr`): Server GC job (`Server=true`)
- `wks` (`net10-wks`): Workstation GC job (`Server=false`)

Artifacts:
- Root: `BenchmarkDotNet.Artifacts/spill/20260317-095743`
- Report: `BenchmarkDotNet.Artifacts/20260317-095745/VapeCache.Benchmarks.Benchmarks.SpillStoreBenchmarks-report-github.md`
- CSV: `BenchmarkDotNet.Artifacts/20260317-095745/VapeCache.Benchmarks.Benchmarks.SpillStoreBenchmarks-report.csv`

## Performance Summary (Segmented vs Scatter)

Lower ratio is better (`Ratio < 1.00` means segmented is faster).

### `write->read->delete` ratio range

| Payload | CRC=false | CRC=true |
|---|---:|---:|
| 4KB | `0.05` to `0.08` | `0.06` to `0.07` |
| 64KB | `0.12` to `0.12` | `0.09` to `0.12` |
| 256KB | `0.28` to `0.28` | `0.28` to `0.29` |

### `read hit` ratio range

| Payload | CRC=false | CRC=true |
|---|---:|---:|
| 4KB | `0.08` to `0.08` | `0.07` to `0.08` |
| 64KB | `0.21` to `0.23` | `0.24` to `0.27` |
| 256KB | `0.51` to `0.70` | `0.58` to `0.63` |

### `write (ring update)` ratio range

| Payload | CRC=false | CRC=true |
|---|---:|---:|
| 4KB | `0.08` to `0.09` | `0.08` to `0.09` |
| 64KB | `0.14` to `0.16` | `0.10` to `0.18` |
| 256KB | `0.20` to `0.33` | `0.20` to `0.31` |

## GC Churn Summary

### Allocation behavior

- `write (ring update)`: segmented allocates ~`694-698 B` per op versus scatter `5,640 B` (4KB), `67,090 B` (64KB), and `263,752 B` (256KB).
- `write->read->delete`: segmented allocates ~`45-55%` of scatter at all tested payloads.
- `read hit`: segmented allocates near parity at larger payloads (`~99-100%` of scatter), slightly better at 4KB (`~83%`).

### GC generation pressure

- `write (ring update)`: segmented is effectively `0` for `Gen0/Gen1/Gen2` at 64KB and 256KB, while scatter shows substantial Gen churn.
- `write->read->delete`: segmented cuts Gen churn roughly in half for 64KB and 256KB.
- `read hit`: Gen churn is similar between stores at larger payloads (expected due full payload materialization).

## Bottom Line

After the CRC-path optimization, segmented spill is faster than scatter across all tested matrix slices (both `svr` and `wks`, CRC on/off), with materially lower GC churn on write-heavy paths.
