# Grocery Store Head-to-Head (Authoritative)

Generated: 2026-03-04 (America/Chicago)
Scenario: locked apples + optimized tracks, 10 trials

## Environment

- Redis endpoint: `192.168.100.50:6379` (ACL auth)
- Shoppers: `50,000`
- Max cart size: `40`
- Max degree: `72`
- Mux profile: `FullTilt`
- Mux connections: `12`
- Max in-flight per mux: `8192`
- Coalescing: `true` (adaptive `true`)
- Socket RESP reader: `true`
- Dedicated lane workers: `true`
- Host isolation gate: enabled (`CPU <= 40%` for 6 consecutive samples)

## Summary

| Track | Trials | Vape Median (shoppers/s) | SER Median (shoppers/s) | Median Ratio (Vape/SER) | Ratio of Medians | Ratio CoV | Ratio Spread |
|---|---:|---:|---:|---:|---:|---:|---:|
| ApplesToApples | 10 | 23,808 | 25,605 | 0.920 | 0.930 | 7.6% | 0.767 .. 0.990 |
| OptimizedProductPath | 10 | 26,795 | 25,586 | 1.040 | 1.050 | 7.1% | 0.879 .. 1.121 |

## Optimized Track Trial Results

| Run | Vape Throughput | SER Throughput | Ratio |
|---|---:|---:|---:|
| 1 | 26,994.18 | 25,842.44 | 1.045 |
| 2 | 22,997.48 | 25,141.74 | 0.915 |
| 3 | 26,596.24 | 25,542.44 | 1.041 |
| 4 | 26,256.01 | 25,630.39 | 1.024 |
| 5 | 27,913.77 | 25,965.73 | 1.075 |
| 6 | 26,997.58 | 25,031.53 | 1.079 |
| 7 | 28,108.14 | 25,066.80 | 1.121 |
| 8 | 22,800.22 | 25,940.42 | 0.879 |
| 9 | 27,031.54 | 26,171.23 | 1.033 |
| 10 | 26,351.17 | 24,946.33 | 1.056 |

## Verdict

- Hot path (`OptimizedProductPath`): **VapeCache is ahead** in this locked 10-trial run.
- Parity path (`ApplesToApples`): **VapeCache is behind** in this run.
- Messaging guidance: claim hot-path performance separately from parity/fallback behavior.
