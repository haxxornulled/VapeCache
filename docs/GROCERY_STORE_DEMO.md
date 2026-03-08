# Grocery Store Stress Test Demo 🛒

**Black Friday Mode: 10,000 Concurrent Shoppers!**

## Overview

This comprehensive stress test simulates a high-traffic grocery store using **all of VapeCache's typed collection APIs**:
- **Shopping Carts** (LIST operations)
- **Flash Sale Participants** (SET operations)
- **User Sessions** (HASH operations)
- **Product Inventory** (Simple cache with get/set)

## What It Tests

### Stress Test Parameters
- **Config-driven workload** via `GroceryStoreStress` options
- **Default (appsettings.json):** 100,000 shoppers, 2,000 concurrency, 180s target duration
- **Development (appsettings.Development.json):** 5,000 shoppers, 200 concurrency, 30s target duration
- **Real-world shopping patterns** (browse → cart → checkout)
- **Flash sales** with limited inventory
- **Live metrics** reported every 10 seconds

### Cache Operations Tested
1. **LIST Operations**
   - `LPUSH` - Add items to front of cart
   - `RPOP` - Remove items from back of cart
   - `LRANGE` - View all cart items
   - `LLEN` - Get cart item count

2. **SET Operations**
   - `SADD` - Join flash sale (idempotent)
   - `SISMEMBER` - Check if user in flash sale (O(1))
   - `SMEMBERS` - Get all participants
   - `SCARD` - Get participant count
   - `SREM` - Leave flash sale

3. **HASH Operations**
   - `HSET` - Save user session
   - `HGET` - Get user session
   - `HMGET` - Get multiple sessions

4. **Simple Cache**
   - `GET/SET` - Product catalog caching
   - TTL expiration
   - Cache-aside pattern

## Shopper Behavior Simulation

Each simulated shopper performs realistic actions:

```
70% - Browse 10-25 products (cache hits/misses)
30% - Join a flash sale (SET operations)
50% - Add 15-35 items to cart (LIST operations)
30% - View cart (LRANGE)
20% - Checkout/clear cart (single key delete)
10% - Remove item from cart (RPOP)
```

## Running the Demo

### Prerequisites
1. **Redis** (optional - works with in-memory fallback)
   ```bash
   docker run -p 6379:6379 redis:latest
   ```

2. **.NET 10**
   ```bash
   dotnet --version  # Should be 10.0 or later
   ```

### Running
```bash
cd VapeCache.Console
dotnet run
```

### Head-to-Head Comparison Mode (VapeCache vs StackExchange.Redis)

```powershell
$env:VAPECACHE_MAX_CART_SIZE = "40"
$env:VAPECACHE_BENCH_TRACK = "both" # optimized | apples | both
$env:VAPECACHE_BENCH_MAX_DEGREE = "80"
$env:VAPECACHE_BENCH_MUX_PROFILE = "FullTilt"
$env:VAPECACHE_BENCH_MUX_CONNECTIONS = "8"
$env:VAPECACHE_BENCH_MUX_INFLIGHT = "8192"
$env:VAPECACHE_BENCH_MUX_COALESCE = "true"
$env:VAPECACHE_BENCH_MUX_RESPONSE_TIMEOUT_MS = "0"
$env:DOTNET_GCServer = "1"
"2" | dotnet run -c Release -- --compare
```

This runs the 50,000 shopper scenario and prints side-by-side throughput and latency.
Track semantics:
- `apples`: command/payload parity with StackExchange.Redis (JSON cart/session payloads)
- `optimized`: VapeCache optimized cart/session path
- `both`: prints both tracks in one run so hot-path and parity are both visible
Reporting guidance:
- Use `optimized` (or `both` -> `OptimizedProductPath`) for hot-path comparison claims.
- Use `apples` (or `both` -> `ApplesToApples`) for parity/fallback behavior claims.

For repeatable medians + pass/fail gating:

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-grocery-head-to-head.ps1 `
  -Trials 5 `
  -ShopperCount 50000 `
  -MaxCartSize 40 `
  -MaxDegree 72 `
  -Track both `
  -MuxProfile FullTilt `
  -MuxConnections 8 `
  -MuxInFlight 8192 `
  -ServerGc true `
  -RedisHost 127.0.0.1 `
  -RedisPort 6379 `
  -FailBelowRatio 1.0
```
`run-grocery-head-to-head.ps1` builds `Release` binaries before running trials unless `-SkipBuild` is provided.

### Claim-Safe Reporting Modes

Use one of these two modes and label reports explicitly:

1. **Strict/Fair (authoritative)**  
   Use `-DisableTrackDefaults` so both tracks/providers share the same mux knobs.

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-grocery-head-to-head.ps1 `
  -Trials 5 `
  -Track both `
  -DisableTrackDefaults `
  -ShopperCount 50000 `
  -MaxCartSize 40 `
  -FailBelowRatio 1.0
```

2. **Tuned/Showcase (engineering)**  
   Leave track defaults enabled to show workload-tuned potential.

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-grocery-head-to-head.ps1 `
  -Trials 5 `
  -Track both `
  -ShopperCount 50000 `
  -MaxCartSize 40 `
  -FailBelowRatio 1.0
```

Benchmark defaults tuned for high-core hosts:
- `MaxDegree=72` (stability-first default for 50k shopper runs on modern multi-core hosts).
- `MuxProfile=FullTilt`, `MuxInFlight=8192`, `MuxCoalesce=true`.
- Track-aware mux defaults are enabled by default:
  - `optimized`: `MuxConnections=8`, `MuxAdaptiveCoalescing=true`
  - `apples`: `MuxConnections=1`, `MuxAdaptiveCoalescing=false`
- `CleanupRunKeys=true` to prevent key buildup and Redis memory-pressure drift across trials.
- `Track=both` runs apples and optimized in isolated passes per trial (no shared-run coupling).
- `ServerGc=true` (`DOTNET_GCServer=1`) for steadier throughput under load.
- For a fixed 28-core box, `-MaxDegree 68..80` is the preferred stability window for 50k x 40 runs.
- For peak throughput sweeps, test `-MaxDegree 80` after stability is confirmed.
Pass `-DisableTrackDefaults` to force a single mux profile/connection strategy across both tracks.
To use the exact same Redis setting source as the Grocery Store host, pass the connection string directly:
- URI style is supported: `redis://user:pass@host:port/db` (or `rediss://...`).
- StackExchange style is also supported: `host:port,user=...,password=...,ssl=...`.

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-grocery-head-to-head.ps1 `
  -Trials 5 `
  -ShopperCount 50000 `
  -Track both `
  -RedisConnectionString "redis://localhost:6379/0"
```

### Fast Dogfood Run (Recommended)

```powershell
powershell -ExecutionPolicy Bypass -File VapeCache.Console/run-grocery-dogfood.ps1 `
  -ConnectionString "redis://localhost:6379/0" `
  -ConcurrentShoppers 200 `
  -TotalShoppers 5000 `
  -TargetDurationSeconds 30 `
  -Profile FullTilt `
  -EnablePluginDemo
```

### Hot-Key Stampede Run (300k / 70 Items)

```powershell
powershell -ExecutionPolicy Bypass -File VapeCache.Console/run-grocery-stampede.ps1 `
  -RedisHost 192.168.100.50 `
  -RedisPort 6379 `
  -RedisUsername admin `
  -RedisPassword "your-password"
```

This script applies a tuned multiplexer profile and a hot-product workload:
- 300,000 shoppers
- 4,000 concurrency
- 70 cart items per shopper
- 100% bias to one hot product key
- host auto-stop on completion

### Expected Output

```
==================================================
  GROCERY STORE STRESS TEST - BLACK FRIDAY MODE
==================================================
Concurrent Shoppers: 4000
Total Shoppers: 300000
Target Duration: 240 seconds
Redis Modules Detected: timeseries, vectorset, bf, ReJSON, search
Created 5 flash sales
Starting stress test in 1 seconds...

==================================================
  STRESS TEST COMPLETE
==================================================
Total Shoppers Simulated: 300000
Total Duration: 21.32 seconds
Throughput: 14071 shoppers/sec

Sample Cached Data:
  Flash Sale 'Frozen Pizza': 0 participants
  Sample Cart (user-000042): 301 items

Final Cache Statistics:
  Total Gets: 300006
  Total Sets: 8
  Total Hits: 300003
  Total Misses: 3
  Hit Rate: 100.00%
  Total Removes: 0
  Fallback Events: 0
  Circuit Breaker Opens: 0
```

## What's Being Cached

### Product Catalog (Simple Cache)
```csharp
// 25 products across 6 categories
- Fresh Produce (bananas, strawberries, lettuce, avocados, carrots)
- Dairy (milk, yogurt, cheese, butter, eggs)
- Bakery (bread, croissants, bagels)
- Meat & Seafood (chicken, beef, salmon)
- Pantry (pasta, olive oil, tomatoes, rice)
- Beverages (orange juice, water, coffee)
- Frozen (ice cream, pizza)

// Cached for 10 minutes with automatic reload on miss
```

### Shopping Carts (LIST per user)
```csharp
// Key: "cart:{userId}"
// Type: LIST of CartItem
// Operations:
- PushFrontAsync() → LPUSH (add to front)
- PopBackAsync() → RPOP (remove from back)
- RangeAsync(0, -1) → LRANGE (view all)
- LengthAsync() → LLEN (count items)
```

### Flash Sale Participants (SET per sale)
```csharp
// Key: "sale:{saleId}:participants"
// Type: SET of userId
// Operations:
- AddAsync() → SADD (join sale)
- ContainsAsync() → SISMEMBER (check membership)
- MembersAsync() → SMEMBERS (get all participants)
- CountAsync() → SCARD (participant count)
- RemoveAsync() → SREM (leave sale)
```

### User Sessions (HASH)
```csharp
// Key: "sessions:active"
// Type: HASH with sessionId → UserSession
// Operations:
- SetAsync() → HSET (save session)
- GetAsync() → HGET (load session)
- GetManyAsync() → HMGET (batch load)
```

## Performance Expectations

### With Redis
- **Throughput (FullTilt profile):** typically 5,000+ shoppers/sec on tuned settings.
- **Throughput (hot-key stampede profile):** typically 10,000+ shoppers/sec.
- **Hit Rate:** can approach 100% when traffic is heavily hot-key biased.
- **Latency:** low single-digit milliseconds for most operations.

### In-Memory Fallback
- **Throughput:** generally high, but varies with local CPU and GC pressure.
- **Hit Rate:** workload-dependent (same logic, different backend).
- **Latency:** typically lower than remote Redis in local-only mode.

## Code Highlights

### Typed Collections Make It Easy

**Before** (manual byte[] handling):
```csharp
var buffer = new ArrayBufferWriter<byte>();
JsonSerializer.Serialize(buffer, cartItem);
await executor.LPushAsync("cart:user123", buffer.WrittenMemory, ct);

var bytes = await executor.LPopAsync("cart:user123", ct);
var item = bytes != null ? JsonSerializer.Deserialize<CartItem>(bytes) : null;
```

**After** (typed collections):
```csharp
var cart = collections.List<CartItem>("cart:user123");
await cart.PushFrontAsync(cartItem);
var item = await cart.PopFrontAsync();  // Returns CartItem?, not byte[]!
```

### Flash Sale Example

```csharp
// Create flash sale
var sale = await store.CreateFlashSaleAsync(
    "prod-001",           // Product ID
    1.49m,                // Sale price (50% off)
    100,                  // Limited quantity
    TimeSpan.FromMinutes(10));

// User joins sale
await store.JoinFlashSaleAsync(sale.Id, "user-12345");

// Check if user already in sale (O(1) lookup!)
var isParticipant = await store.IsInFlashSaleAsync(sale.Id, "user-12345");

// Get participant count
var count = await store.GetFlashSaleParticipantCountAsync(sale.Id);
```

## Files

| File | Purpose |
|------|---------|
| [Models.cs](../VapeCache.Console/GroceryStore/Models.cs) | Domain models (Product, CartItem, FlashSale, etc.) |
| [GroceryStoreService.cs](../VapeCache.Console/GroceryStore/GroceryStoreService.cs) | Cache service using typed collections |
| [GroceryStoreStressTest.cs](../VapeCache.Console/GroceryStore/GroceryStoreStressTest.cs) | Stress test simulation (configurable workload) |
| [GroceryStoreStressOptions.cs](../VapeCache.Console/GroceryStore/GroceryStoreStressOptions.cs) | Runtime knobs for concurrency and workload size |
| [run-grocery-dogfood.ps1](../VapeCache.Console/run-grocery-dogfood.ps1) | Repeatable dogfood execution script |
| [run-grocery-stampede.ps1](../VapeCache.Console/run-grocery-stampede.ps1) | Tuned hot-key stampede script (300k/70 baseline) |
| [PLUGINS.md](../VapeCache.Console/PLUGINS.md) | Plugin extension pattern used by console host |
| [Program.cs](../VapeCache.Console/Program.cs) | DI registration and startup |

## Customization

Tune workload intensity directly from `GroceryStoreStress` settings:

```json
"GroceryStoreStress": {
  "ConcurrentShoppers": 2000,
  "TotalShoppers": 100000,
  "StopHostOnCompletion": true,
  "BrowseChancePercent": 70,
  "BrowseMinProducts": 10,
  "BrowseMaxProducts": 25,
  "AddToCartChancePercent": 50,
  "CartItemsMin": 15,
  "CartItemsMax": 35,
  "HotProductId": "prod-025",
  "HotProductBiasPercent": 100
}
```

Or pick a runnable profile:

```powershell
powershell -ExecutionPolicy Bypass -File VapeCache.Console/run-grocery-dogfood.ps1 `
  -Profile FullTilt
```

## Observability

The demo integrates with:
- **OpenTelemetry** - Metrics exported to OTLP endpoint
- **Serilog** - Structured logging
- **CacheStats** - Real-time hit/miss tracking
- **Circuit Breaker** - Automatic Redis fallback

View metrics in:
- **.NET Aspire Dashboard** - `http://localhost:15888`
- **Grafana** - If configured
- **Console Output** - Real-time stats every 10 seconds

## Testing Failure Scenarios

### Test 1: Redis Fails Mid-Test
```bash
# Start the test
dotnet run

# After 30 seconds, stop Redis
docker stop <redis-container-id>

# Watch it auto-fallback to in-memory with zero downtime!
```

**Expected:** Circuit breaker opens, all operations continue using in-memory executor

### Test 2: Redis Comes Back
```bash
# Restart Redis
docker start <redis-container-id>

# Watch it auto-recover after next health check!
```

**Expected:** Circuit breaker closes after successful probe, switches back to Redis

## Key Takeaways

✅ **Type-Safe** - No manual serialization, compile-time safety
✅ **Zero-Allocation** - IBufferWriter patterns throughout
✅ **Resilient** - Automatic Redis → In-Memory fallback
✅ **Fast** - 1000+ concurrent operations with ease
✅ **Observable** - Real-time metrics and logging
✅ **Realistic** - Simulates actual e-commerce patterns

Use both comparison tracks when reporting against StackExchange.Redis:
- `apples` for parity
- `optimized` for VapeCache hot-path capability

---

**Questions?** See [TYPED_COLLECTIONS.md](TYPED_COLLECTIONS.md) for API details or [PHASE_2_3_COMPLETE.md](PHASE_2_3_COMPLETE.md) for implementation guide.
