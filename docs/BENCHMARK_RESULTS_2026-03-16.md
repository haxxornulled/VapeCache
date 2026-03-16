# Benchmark Results (March 16, 2026)

This snapshot summarizes Redis head-to-head runs comparing VapeCache and `StackExchange.Redis` with equal write/read operation counts.

## Primary Publish Set: Soak Profile, Median of 3 Trials

Profile settings: `Pairs=200,000`, `WarmupPairs=20,000`, `KeySpace=5,000`, `PayloadBytes=1,024`

| Data Type | VapeCache Ops/sec | SER Ops/sec | Ratio (Vape/SER) | Vape Avg us/op | SER Avg us/op |
|---|---:|---:|---:|---:|---:|
| String | 8943.964 | 7716.907 | 1.159x | 111.807 | 129.586 |
| Hash | 9396.131 | 8065.162 | 1.165x | 106.427 | 123.990 |
| List | 9647.309 | 8173.963 | 1.180x | 103.656 | 122.340 |
| Set | 9544.504 | 8435.693 | 1.131x | 104.772 | 118.544 |
| SortedSet | 9407.646 | 8375.568 | 1.123x | 106.297 | 119.395 |

Geometric mean ratio across core types: **1.151x**

## Cross-Check Set: CI Profile, Median of 11 Trials

Profile settings: `Pairs=2,000`, `WarmupPairs=200`, `KeySpace=200`, `PayloadBytes=256`

| Data Type | VapeCache Ops/sec | SER Ops/sec | Ratio (Vape/SER) |
|---|---:|---:|---:|
| String | 9394.441 | 8416.132 | 1.116x |
| Hash | 9270.536 | 8579.436 | 1.081x |
| List | 9411.898 | 7639.796 | 1.232x |
| Set | 8996.040 | 7299.367 | 1.232x |
| SortedSet | 8535.165 | 8262.334 | 1.033x |

## SortedSet Regression Fix Validation

- Before optimization (ci, median-5): `0.934x`
- After optimization (soak, median-3): `1.123x`

All runs used equal write/read operation counts for both clients.
