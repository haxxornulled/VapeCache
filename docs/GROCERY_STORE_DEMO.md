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
$env:VAPECACHE_BENCH_TRACK = "optimized" # optimized | apples | both
$env:VAPECACHE_BENCH_MUX_CONNECTIONS = "4"
$env:VAPECACHE_BENCH_MUX_INFLIGHT = "8192"
$env:VAPECACHE_BENCH_MUX_COALESCE = "true"
$env:VAPECACHE_BENCH_MUX_RESPONSE_TIMEOUT_MS = "0"
"2" | dotnet run -c Release -- --compare
```

This runs the 50,000 shopper scenario and prints side-by-side throughput and latency.

For repeatable medians + pass/fail gating:

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-grocery-head-to-head.ps1 `
  -Trials 5 `
  -ShopperCount 50000 `
  -MaxCartSize 40 `
  -Track optimized `
  -FailBelowRatio 1.0
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

### Expected Output

```
==================================================
  GROCERY STORE STRESS TEST - BLACK FRIDAY MODE
==================================================
Concurrent Shoppers: 1000
Total Shoppers: 10000
Test Duration: 120 seconds
Redis Modules Detected: (none - vanilla Redis)
Created 5 flash sales
Starting stress test in 3 seconds...

[10s] Cache Stats - Gets: 45,234 | Sets: 12,456 | Hits: 38,901 (86.0%) | Misses: 6,333
[20s] Cache Stats - Gets: 92,567 | Sets: 25,123 | Hits: 79,456 (85.8%) | Misses: 13,111
[30s] Cache Stats - Gets: 138,234 | Sets: 37,890 | Hits: 118,901 (86.0%) | Misses: 19,333
...

==================================================
  STRESS TEST COMPLETE
==================================================
Total Shoppers Simulated: 10,000
Total Duration: 120.45 seconds
Throughput: 83 shoppers/sec

Sample Cached Data:
  Flash Sale 'Organic Bananas': 2,847 participants
  Sample Cart (user-000042): 3 items

Final Cache Statistics:
  Total Gets: 547,234
  Total Sets: 182,456
  Total Hits: 471,123
  Total Misses: 76,111
  Hit Rate: 86.09%
  Total Removes: 12,456
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
- **Throughput:** 80-100 shoppers/sec
- **Hit Rate:** 85-90% (products cached after first load)
- **Latency:** <5ms per operation
- **Memory:** ~50MB Redis memory usage

### In-Memory Fallback
- **Throughput:** 200-300 shoppers/sec (faster!)
- **Hit Rate:** 85-90% (same)
- **Latency:** <1ms per operation (60x faster)
- **Memory:** ~30MB application memory

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
| [PLUGINS.md](../VapeCache.Console/PLUGINS.md) | Plugin extension pattern used by console host |
| [Program.cs](../VapeCache.Console/Program.cs) | DI registration and startup |

## Customization

Tune workload intensity directly from `GroceryStoreStress` settings:

```json
"GroceryStoreStress": {
  "ConcurrentShoppers": 2000,
  "TotalShoppers": 100000,
  "BrowseChancePercent": 70,
  "BrowseMinProducts": 10,
  "BrowseMaxProducts": 25,
  "AddToCartChancePercent": 50,
  "CartItemsMin": 15,
  "CartItemsMax": 35
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

This is what makes VapeCache amazing - you get Redis-level features with in-memory-level resilience and StackExchange.Redis-beating performance! 🚀

---

**Questions?** See [TYPED_COLLECTIONS.md](TYPED_COLLECTIONS.md) for API details or [PHASE_2_3_COMPLETE.md](PHASE_2_3_COMPLETE.md) for implementation guide.
