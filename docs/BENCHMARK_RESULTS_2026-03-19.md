# Benchmark Results (March 19, 2026)

This report captures the GroceryStore comparison refresh with both required report classes:

- **Strict/Fair (authoritative)**: track defaults disabled.
- **Tuned/Showcase (engineering)**: track defaults enabled.

## Metadata

- Date/time (America/Chicago):
  - Strict/Fair run window: approximately `2026-03-19 23:08` to `23:11`
  - Tuned/Showcase run window: approximately `2026-03-19 23:11` to `23:19`
- Commit: `2fc7e49`
- Track: `both` (`ApplesToApples` + `OptimizedProductPath`)
- Track defaults:
  - Strict/Fair: disabled (`-DisableTrackDefaults`)
  - Tuned/Showcase: enabled
- Redis endpoint class: direct Debian endpoint (`benchmark-redis.example.internal:6379`)
- Redis auth mode: ACL (`admin` user, password redacted)
- Redis hardening posture:
  - `bind 127.0.0.1 benchmark-redis.example.internal`
  - `protected-mode yes`
  - `default` user disabled
  - nftables restricts `6379` to benchmark host `benchmark-client.example.internal`
- Host/runtime summary:
  - OS: Windows 10.0.26200 (`win-x64`)
  - .NET SDK: 10.0.201
  - .NET host/runtime: 10.0.5
- Run counts:
  - Outer script trials: 5
  - Inner harness per trial: 1 warmup + 3 measured runs (from app defaults)

Artifacts:

- `artifacts/grocery-refresh-20260319-230807/strict-fair-17223-6379-acl.log`
- `artifacts/grocery-refresh-20260319-230807/tuned-showcase-17223-6379-acl.log`

## Commands

Strict/Fair:

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-grocery-head-to-head.ps1 `
  -Trials 5 `
  -ShopperCount 50000 `
  -MaxCartSize 40 `
  -Track both `
  -VapeExecutorMode raw `
  -DisableTrackDefaults `
  -RedisHost benchmark-redis.example.internal `
  -RedisPort 6379 `
  -RedisUsername admin `
  -RedisPassword <redacted> `
  -FailBelowRatio 0.0
```

Tuned/Showcase:

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-grocery-head-to-head.ps1 `
  -Trials 5 `
  -ShopperCount 50000 `
  -MaxCartSize 40 `
  -Track both `
  -VapeExecutorMode raw `
  -RedisHost benchmark-redis.example.internal `
  -RedisPort 6379 `
  -RedisUsername admin `
  -RedisPassword <redacted> `
  -FailBelowRatio 0.0
```

## Strict/Fair Results

Track summary (vs SER):

| Track | Trials | Vape Median (shoppers/sec) | SER Median (shoppers/sec) | Median Ratio | Ratio Of Medians | Ratio CoV | Ratio Spread |
|---|---:|---:|---:|---:|---:|---:|---|
| ApplesToApples | 5 | 23,159 | 36,418 | 0.620 | 0.636 | 7.1% | 0.548 .. 0.670 |
| OptimizedProductPath | 5 | 33,901 | 35,212 | 0.928 | 0.963 | 4.5% | 0.879 .. 0.974 |

Aggregated line from script:

- Median Vape throughput: `33,901 shoppers/sec`
- Median SER throughput: `35,212 shoppers/sec`
- Median ratio (Vape/SER): `0.963`

## Tuned/Showcase Results

Track summary (vs SER):

| Track | Trials | Vape Median (shoppers/sec) | SER Median (shoppers/sec) | Median Ratio | Ratio Of Medians | Ratio CoV | Ratio Spread |
|---|---:|---:|---:|---:|---:|---:|---|
| ApplesToApples | 5 | 6,838 | 6,525 | 1.033 | 1.048 | 5.3% | 0.944 .. 1.070 |
| OptimizedProductPath | 5 | 14,274 | 10,446 | 1.366 | 1.366 | 5.3% | 1.318 .. 1.492 |

Aggregated line from script:

- Median Vape throughput: `14,274 shoppers/sec`
- Median SER throughput: `10,446 shoppers/sec`
- Median ratio (Vape/SER): `1.366`

## Interpretation Notes

- This run demonstrates why report class labeling is mandatory:
  - Strict/Fair and Tuned/Showcase tell materially different stories on the same machine.
- Reporting split still applies:
  - `OptimizedProductPath` for hot-path capability claims.
  - `ApplesToApples` for parity/fallback behavior claims.
- Security posture note:
  - Remote endpoint benchmarking now uses ACL authentication and host-restricted firewall policy.
