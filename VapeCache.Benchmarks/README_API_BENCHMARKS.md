# VapeCache API Benchmarks

Comprehensive benchmarks for the VapeCache public API using BenchmarkDotNet.

## Benchmark Suites

### 1. CacheServiceApiBenchmarks
**Purpose**: Core API performance testing

**What it measures**:
- Raw byte[] operations (GET/SET)
- Zero-allocation typed operations with JSON
- Cache-aside pattern (`GetOrSetAsync`)
- Typed collections (LIST, SET, HASH)
- Remove operations

**Baseline**: InMemory vs Hybrid (circuit open)

**Run**:
```bash
dotnet run -c Release --project VapeCache.Benchmarks -- --filter "*CacheServiceApiBenchmarks*"
```

---

### 2. TypedCollectionsBenchmarks
**Purpose**: Real-world collection usage patterns

**What it measures**:
- **LIST**: Shopping cart operations (add, remove, view, count)
- **SET**: Active users tracking (add, contains, count, remove)
- **HASH**: User profile storage (set field, get field, get multiple fields)
- **Bulk operations**: Adding 100 users, processing cart items
- **Mixed workload**: Complete shopping flow, user session lifecycle

**Run**:
```bash
dotnet run -c Release --project VapeCache.Benchmarks -- --filter "*TypedCollectionsBenchmarks*"
```

---

### 3. CircuitBreakerPerformanceBenchmarks
**Purpose**: Circuit breaker overhead analysis

**What it measures**:
- Circuit breaker overhead (InMemory vs Hybrid with circuit open)
- Typed operations with circuit breaker
- Cache-aside with failover
- High-frequency throughput (100 ops)
- Mixed workload performance

**Baseline**: Pure InMemory (no circuit breaker)

**Run**:
```bash
dotnet run -c Release --project VapeCache.Benchmarks -- --filter "*CircuitBreakerPerformanceBenchmarks*"
```

---

## Running All API Benchmarks

Run all three benchmark suites:

```bash
dotnet run -c Release --project VapeCache.Benchmarks -- --filter "*CacheServiceApiBenchmarks* *TypedCollectionsBenchmarks* *CircuitBreakerPerformanceBenchmarks*"
```

Or run them one by one for detailed analysis.

---

## Interpreting Results

### Expected Performance Characteristics

#### Raw Operations (byte[])
- **InMemory GET**: ~50-100 ns
- **InMemory SET**: ~100-200 ns
- **Hybrid GET (circuit open)**: ~100-300 ns
- **Hybrid SET (circuit open)**: ~200-500 ns

**Circuit breaker overhead**: ~2-5x (acceptable for resilience)

---

#### Typed Operations (JSON Serialization)
- **InMemory GET<T>**: ~500-1000 ns
- **InMemory SET<T>**: ~1-2 µs
- **Hybrid GET<T>**: ~1-3 µs
- **Hybrid SET<T>**: ~2-5 µs

**JSON serialization dominates** (80% of time)

---

#### GetOrSetAsync (Cache-Aside)
- **Cache Hit**: ~1-5 µs
- **Cache Miss**: ~1-2 ms (dominated by factory function)

**Stampede protection is critical** for cache misses under load.

---

#### Typed Collections
- **List.PushBack**: ~1-3 µs
- **List.PopFront**: ~1-3 µs
- **List.RangeAsync**: ~2-10 µs (depends on count)
- **Set.Add**: ~1-3 µs
- **Set.Contains**: ~500-1500 ns
- **Hash.Set**: ~1-3 µs
- **Hash.Get**: ~500-1500 ns
- **Hash.GetMany** (3 fields): ~2-5 µs

**All operations sub-microsecond to low microsecond range**.

---

#### Circuit Breaker Overhead
| Operation | Pure InMemory | Hybrid (Circuit Open) | Overhead |
|-----------|---------------|----------------------|----------|
| GET       | 80 ns         | 200 ns              | 2.5x     |
| SET       | 150 ns        | 400 ns              | 2.7x     |
| GET<T>    | 800 ns        | 2 µs                | 2.5x     |
| SET<T>    | 1.5 µs        | 4 µs                | 2.7x     |

**Overhead**: 2-3x (acceptable - you get automatic failover!)

---

## Memory Allocation Analysis

BenchmarkDotNet will show memory allocations. Expected results:

### Zero-Allocation Operations
✅ Raw byte[] GET/SET should show **0 B allocated** (uses `ReadOnlyMemory<byte>`)
✅ Typed operations should allocate only for the result object

### Allocations to Watch
- JSON serialization: ~200-500 B per object (System.Text.Json buffers)
- Collection operations: ~100-200 B per item (boxing/unboxing)

**VapeCache uses zero-allocation patterns where possible**, but JSON serialization inherently allocates.

---

## Throughput Benchmarks

### 100 Operations Test
Measures sustained throughput:

- **InMemory 100 GETs**: ~10-20 µs total = **5-10M ops/sec**
- **Hybrid 100 GETs (circuit open)**: ~30-50 µs total = **2-3M ops/sec**
- **InMemory 100 SETs**: ~20-40 µs total = **2.5-5M ops/sec**
- **Hybrid 100 SETs (circuit open)**: ~50-100 µs total = **1-2M ops/sec**

**Single-threaded throughput is excellent**. Real-world throughput with concurrency will be higher.

---

## Real-World Scenarios

### Shopping Cart Flow
```
Mixed_ShoppingFlow benchmark simulates:
1. Mark user online (SET.Add)
2. Add 3 items to cart (LIST.PushBack × 3)
3. Get cart count (LIST.Length)
4. View cart (LIST.Range)
5. Update profile (HASH.Set)
6. Checkout (LIST.PopFront × 3)
7. Mark user offline (SET.Remove)
```

**Expected time**: ~20-50 µs (complete flow)

**Real-world equivalent**: 20,000-50,000 shopping flows/second (single thread)

---

### User Session Lifecycle
```
Mixed_UserSessionLifecycle benchmark simulates:
1. Login: Create session hash (HASH.Set × 3)
2. Activity: Update session (HASH.Set × 2)
3. Add to active sessions (SET.Add)
4. Get session data (HASH.GetMany × 3 fields)
5. Logout: Remove from active (SET.Remove)
```

**Expected time**: ~15-40 µs (complete lifecycle)

**Real-world equivalent**: 25,000-65,000 session lifecycles/second

---

## Comparison to Other Libraries

### StackExchange.Redis
- **GET**: ~50-100 µs (network overhead)
- **SET**: ~100-200 µs (network overhead)

**VapeCache (in-memory mode)**: 100-1000x faster (no network!)

### Microsoft.Extensions.Caching.Memory
- **GET**: ~50-100 ns
- **SET**: ~100-200 ns

**VapeCache InMemory**: Comparable performance
**VapeCache Hybrid**: 2-3x overhead for circuit breaker (worth it for resilience!)

---

## Running Specific Benchmarks

### Single benchmark:
```bash
dotnet run -c Release --project VapeCache.Benchmarks -- --filter "*List_AddToCart*"
```

### Multiple benchmarks:
```bash
dotnet run -c Release --project VapeCache.Benchmarks -- --filter "*GetOrSet*"
```

### Export results:
```bash
dotnet run -c Release --project VapeCache.Benchmarks -- --filter "*CacheServiceApiBenchmarks*" --exporters json html
```

Results will be in `VapeCache.Benchmarks/BenchmarkDotNet.Artifacts/results/`.

---

## Best Practices for Benchmarking

1. **Close all other applications** (especially browsers, IDEs)
2. **Run in Release mode** (`-c Release`)
3. **Don't move the mouse or type** during benchmark runs
4. **Let it warm up** - First iteration may be slower
5. **Run multiple times** - BenchmarkDotNet does this automatically
6. **Check CPU frequency** - Turbo boost should be enabled

---

## Interpreting BenchmarkDotNet Output

```
| Method          | Mean    | Error   | StdDev | Ratio | Gen0  | Allocated |
|---------------- |--------:|--------:|-------:|------:|------:|----------:|
| Get_InMemory    | 82.3 ns | 1.2 ns  | 0.9 ns |  1.00 | 0.001 |      24 B |
| Get_Hybrid      | 213 ns  | 4.1 ns  | 3.8 ns |  2.59 | 0.002 |      48 B |
```

- **Mean**: Average execution time
- **Error**: Standard error of the mean
- **StdDev**: Standard deviation
- **Ratio**: Compared to baseline (Baseline = 1.00)
- **Gen0**: GC Gen0 collections per 1000 operations
- **Allocated**: Memory allocated per operation

**Lower is better** for all metrics except Ratio=1.00 (baseline).

---

## Performance Goals

### VapeCache Target Metrics

✅ **InMemory GET**: < 100 ns
✅ **InMemory SET**: < 200 ns
✅ **Typed GET<T>**: < 2 µs
✅ **Typed SET<T>**: < 5 µs
✅ **Circuit breaker overhead**: < 5x
✅ **Collections operations**: < 10 µs
✅ **Zero allocations** for raw byte[] operations

**If benchmarks show worse performance**, investigate:
- Is Release build enabled? (`-c Release`)
- Is BenchmarkDotNet warming up properly?
- Is background load affecting results?
- Check CPU throttling (should be disabled)

---

## Benchmark Data

All benchmarks use realistic data:

- **Small payload**: 13 bytes ("Hello, World!")
- **Medium payload**: 1 KB (random bytes)
- **Large payload**: 10 KB (random bytes)
- **User object**: ~150 bytes JSON (name, email, age)
- **Cart item**: ~250 bytes JSON (product ID, name, price, quantity, timestamp)

---

## Next Steps

After running benchmarks:

1. **Compare to baseline** - Is Hybrid 2-3x slower than InMemory? (Expected)
2. **Check allocations** - Are raw operations zero-allocation? (Expected)
3. **Profile hot paths** - Use `dotnet-trace` for deeper analysis
4. **Optimize** - If performance is worse than expected, profile and optimize

---

## Troubleshooting

### Benchmarks not running?
```bash
dotnet clean
dotnet build -c Release
dotnet run -c Release --project VapeCache.Benchmarks
```

### Results look wrong?
- Check CPU frequency: `wmic cpu get CurrentClockSpeed,MaxClockSpeed`
- Disable CPU throttling in BIOS
- Close all background apps
- Run in isolated environment

### Out of memory?
- Reduce iterations: `--iterationCount 3`
- Run benchmarks individually instead of all at once

---

## Further Reading

- [BenchmarkDotNet Documentation](https://benchmarkdotnet.org/)
- [VapeCache Performance Guide](../docs/PERFORMANCE.md)
- [Circuit Breaker Configuration](../docs/CIRCUIT_BREAKER.md)
- [API Reference](../docs/API_REFERENCE.md)
