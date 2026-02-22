# VapeCache FAQ

## General

**Q: Does VapeCache expose HTTP endpoints?**  
A: No. VapeCache is a library; endpoint mapping is handled by your host (ASP.NET Core, Aspire, etc.).

**Q: What happens when Redis is down?**  
A: The hybrid cache trips the circuit breaker and falls back to the in-memory executor. Core cache operations keep working, and reconciliation can sync writes back when Redis recovers.

## Redis Modules & Failover

**Q: What happens to module commands during failover?**  
A: Module commands use the in-memory executor when Redis is unavailable. This keeps calls working but with simplified semantics.

- **JSON (RedisJSON)**: Full-document reads/writes work; JSONPath is ignored unless the path is `.`.
- **RediSearch**: Index creation is a no-op; searches return empty results.
- **RedisBloom**: Backed by an in-memory set (exact membership, not probabilistic).
- **RedisTimeSeries**: Backed by an in-memory sorted dictionary.
- **MODULE LIST**: Returns an empty array.
- **PING**: Returns `PONG`.

**Q: Are module fallbacks persisted?**  
A: No. Module fallbacks are in-memory only and reset on process restart.

**Q: How does module detection behave?**  
A: `MODULE LIST` is queried once and cached. If it fails (old Redis or permissions), the detector caches an empty set to avoid retry storms.

## Performance & Benchmarks

**Q: How do I compare to StackExchange.Redis?**  
A: Run the comparison benchmarks:

```bash
$env:VAPECACHE_REDIS_CONNECTIONSTRING = "redis://localhost:6379/0"
dotnet run -c Release --filter *RedisClientHeadToHeadBenchmarks*
dotnet run -c Release --filter *RedisEndToEndHeadToHeadBenchmarks*
```

**Q: How do I benchmark Redis modules?**  
A: Ensure RedisJSON, RediSearch, RedisBloom, and RedisTimeSeries are installed:

```bash
$env:VAPECACHE_REDIS_CONNECTIONSTRING = "redis://localhost:6379/0"
dotnet run -c Release --filter *RedisModuleHeadToHeadBenchmarks*
```

## Configuration & DI

**Q: Do I have to use Microsoft DI?**  
A: No. Autofac is supported via the provided modules; Microsoft DI also works.

**Q: Where do Redis credentials come from?**  
A: Use environment variables or configuration binding. The console host supports an env-var indirection for secrets.
