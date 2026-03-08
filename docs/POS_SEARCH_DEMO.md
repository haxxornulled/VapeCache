# POS Search Demo (RediSearch + SQLite Fallback)

This demo simulates a cashier POS flow where the app searches Redis first, then falls back to SQLite when needed.

## What it does

- Builds a local SQLite catalog schema (`pos_products`).
- Seeds deterministic products (including `No.2 Pencil HB`, code `PCL-0001`).
- Creates a RediSearch index over Redis HASH documents when module `search`/`ft` is available.
- Executes POS queries in this order:
  1. `FT.SEARCH` (cache)
  2. SQLite query on miss
  3. Backfill Redis HASH docs from SQLite results
  4. Repeat query to show warm cache hit (cashier, code, and UPC paths)

## Run

```powershell
powershell -ExecutionPolicy Bypass -File VapeCache.Console/run-pos-search-demo.ps1 `
  -RedisHost 127.0.0.1 `
  -RedisPort 6379 `
  -RedisIndexName "idx:pos:catalog:demo1" `
  -RedisKeyPrefix "pos:demo1:sku:" `
  -CashierQuery "pencil" `
  -LookupCode "PCL-0001" `
  -LookupUpc "012345678901"
```

If you are using Redis ACL:

```powershell
powershell -ExecutionPolicy Bypass -File VapeCache.Console/run-pos-search-demo.ps1 `
  -RedisHost 192.168.100.50 `
  -RedisPort 6379 `
  -RedisUsername admin `
  -RedisPassword "your-password" `
  -RedisIndexName "idx:pos:catalog:demo1" `
  -RedisKeyPrefix "pos:demo1:sku:"
```

Use a unique index/prefix pair when you want a clean cold-start run (first pass from SQLite, second pass from cache).

## Config section

`VapeCache.Console/appsettings.json` now includes a `PosSearchDemo` section for all runtime knobs.

## Stampede load run

Use the load runner to simulate many cashiers hitting a hot product first (for example, everyone asking for a TV code):

```powershell
powershell -ExecutionPolicy Bypass -File VapeCache.Console/run-pos-search-load.ps1 `
  -RedisHost 192.168.100.50 `
  -RedisPort 6379 `
  -RedisUsername admin `
  -RedisPassword "your-password" `
  -RedisIndexName "idx:pos:catalog:load1" `
  -RedisKeyPrefix "pos:load1:sku:" `
  -Duration "00:02:00" `
  -Concurrency 512 `
  -TargetShoppersPerSecond 2500 `
  -HotQuery "code:TV-0099" `
  -HotQueryPercent 90 `
  -CashierQueryPercent 7 `
  -LookupUpcPercent 3
```

Load knobs live under `PosSearchLoad` in `appsettings.json`.
Recommended stable baseline on this environment: `Concurrency=256`, `TargetShoppersPerSecond=2200`, `HotQueryPercent=90`.

## Auto-ramp mode (find max stable shoppers/s)

Use auto-ramp to run multiple target rates in sequence and stop at the first unstable step:

```powershell
powershell -ExecutionPolicy Bypass -File VapeCache.Console/run-pos-search-load.ps1 `
  -RedisHost 192.168.100.50 `
  -RedisPort 6379 `
  -RedisUsername admin `
  -RedisPassword "your-password" `
  -RedisIndexName "idx:pos:catalog:ramp1" `
  -RedisKeyPrefix "pos:ramp1:sku:" `
  -EnableAutoRamp `
  -RampSteps "1600,2000,2400,2800" `
  -RampStepDuration "00:00:20" `
  -MaxFailurePercent 0.5 `
  -MaxP95Ms 30
```

Ramp stability can classify a step unstable by:
- failure percentage above `MaxFailurePercent`
- p95 latency above `MaxP95Ms`
- Redis circuit breaker opening (when `TreatOpenCircuitAsUnstable=true`)
