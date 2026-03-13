# VapeCache Non-Goals

## Purpose

This document sets clear expectations for VapeCache's scope and positioning so the codebase stays focused and maintainable.

## What VapeCache Is ✅

### Enterprise Redis Transport
- **Production-grade reliability**: Circuit breaker, stampede protection, startup preflight
- **Observable by default**: OpenTelemetry metrics + traces for every operation
- **Predictable memory**: Deterministic buffer ownership, ArrayPool for bulk replies, no LOH spikes

### High-Performance Caching Baseline
- **Ordered multiplexing**: Channel<> + pooled IValueTaskSource beats StackExchange.Redis TCS churn
- **Zero-copy leasing**: `RedisValueLease` for large values without copying
- **Coalesced socket writes**: Batched commands reduce syscall overhead (5-30% faster)

### Hybrid Cache with Intelligent Fallback
- **Redis + in-memory**: Automatic fallback to `IMemoryCache` when Redis is unavailable
- **Circuit breaker**: Stops hammering Redis during outages, auto-recovers
- **Stampede protection**: Coalesce concurrent requests for the same key

### First-Class .NET Integration
- **ILogger<T> abstraction**: Works with Serilog, NLog, or any logging provider
- **IOptions<T> pattern**: Configuration via appsettings.json, environment variables, KeyVault
- **Dependency injection**: Native `IServiceCollection` extensions

## What VapeCache Is NOT ❌

### Full Redis Client
❌ **Not a drop-in replacement for StackExchange.Redis**

VapeCache implements ~20 core caching commands. StackExchange.Redis has 200+.

**Why:** Focused scope ensures high quality, predictable behavior, and maintainable codebase. We optimize for the 80% use case (caching) instead of 100% feature parity.

**Recommendation:** Use VapeCache for caching, StackExchange.Redis for advanced features.

### Full Cluster Orchestration
⚠️ **Partial support only**

Supported now:
- MOVED/ASK redirect handling on core cache-path commands (GET/SET/DEL + lease variants)
- Redirect-aware retries with bounded hop count

Still not in scope:
- Full topology tracking and slot-map orchestration for every command surface
- Cross-shard multi-key orchestration

**Why:** Full cluster orchestration adds substantial complexity and operational surface area. We’re prioritizing reliability for cache-first operations.

**Workaround:** For advanced full-cluster command workflows, pair with StackExchange.Redis.

### Lua Scripting
❌ **No EVAL/EVALSHA commands**

**Why:** Lua scripting has complex failure semantics, script management overhead, and testing challenges. Most caching patterns don't need it.

**Status:** Out of scope for this codebase.

**Workaround:** Use StackExchange.Redis for Lua alongside VapeCache for caching.

### Pub/Sub
⚠️ **Basic PUB/SUB is supported via `IRedisPubSubService` (opt-in package: `VapeCache.Extensions.PubSub`)**

Supported now:
- Dedicated pub/sub service (`IRedisPubSubService`) with:
  - `PublishAsync(...)`
  - `SubscribeAsync(...)`
  - per-subscription bounded delivery queue
  - reconnect + resubscribe behavior

Still out of scope:
- Pattern subscriptions (`PSUBSCRIBE`)
- Delivery guarantees beyond Redis pub/sub semantics
- Treating pub/sub as a durable message broker replacement

**Recommendation:** Use StackExchange.Redis or a broker when you need advanced pub/sub routing/guarantees.

### Streams
❌ **No XADD/XREAD/XGROUP commands**

**Why:** Streams require:
- RESP3 for optimal performance (push messages)
- Complex consumer group state management
- Blocking reads (dedicated connections)

**Status:** Out of scope for this codebase.

**Workaround:** Use StackExchange.Redis or Redis.OM for Streams.

### RESP3 Advanced Features
⚠️ **Core RESP3 support is in; advanced RESP3 features remain scoped**

Supported now:
- HELLO 3 negotiation
- RESP3 parser support for map/set/attribute/push/null/boolean/verbatim/blob-error types
- Push frame handling in reader loops (ignored safely when unrelated to request/response path)

Still not in scope:
- Client-side caching protocol orchestration
- Dedicated RESP3 push subscription workflows beyond request/response handling

### Transactions (MULTI/EXEC)
❌ **No atomic transaction support**

**Why:** Transactions conflict with pipelined multiplexing:
- MULTI/EXEC requires sequential execution
- Multiplexing assumes independent operations
- Locking (WATCH) requires dedicated connection

**Status:** Out of scope for this codebase.

**Workaround:** Use StackExchange.Redis for transactions, or design for eventual consistency.

### Message Broker
❌ **Not a replacement for RabbitMQ, Kafka, or Azure Service Bus**

VapeCache is a **cache**, not a message broker. Use proper message brokers for:
- Durable message queues
- Message ordering guarantees
- Delivery acknowledgments
- Dead-letter queues

**Why:** Different problem domains. Caching optimizes for read-heavy workloads with TTLs. Message brokers optimize for reliable delivery and ordering.

### Session State Provider
❌ **No built-in ASP.NET Core session state integration**

**Why:** Session state is a solved problem - use `Microsoft.Extensions.Caching.StackExchangeRedis` or similar.

**Workaround:** VapeCache's `ICacheService` can be used as a backing store, but integration is manual.

### Object Mapper
❌ **No automatic POCO mapping**

VapeCache uses explicit serialization via `ICacheCodecProvider`. No magic:
- No [RedisField] attributes
- No reflection-based mapping
- No schema versioning

**Why:** Explicit serialization is faster, more predictable, and easier to debug.

**Workaround:** Use `SystemTextJsonCodecProvider` (default) or implement custom `ICacheCodec<T>`.

## API Compatibility Commitment

`VapeCache.Abstractions` follows **semantic versioning**:

- **MAJOR (x.0.0):** Breaking changes (avoid at all costs)
- **MINOR (x.y.0):** New features, backward-compatible
- **PATCH (x.y.z):** Bug fixes only

**Current API surface:** Intentionally focused to allow expansion without breaking changes.

### Stable Contracts (1.x)
- `ICacheService` interface (GetAsync, SetAsync, RemoveAsync, GetOrSetAsync)
- `IVapeCache` interface (typed cache API)
- `IRedisCommandExecutor` interface (command methods)
- `CacheKey`, `CacheEntryOptions`, `RedisConnectionOptions` contracts

### What Can Change (Minor Versions)
- New commands added to `IRedisCommandExecutor`
- New extension methods on `ICacheService`
- New codec implementations (`ICacheCodecProvider`)
- New integration packages (`VapeCache.Extensions.*`)

### What Requires Major Version
- Removing methods from interfaces
- Changing method signatures (parameters, return types)
- Renaming public types
- Changing default behaviors in breaking ways

## Positioning Strategy

VapeCache is an **enterprise Redis transport + hybrid cache**, not a general-purpose Redis client.

**Target Users:**
- ✅ Teams building high-performance .NET applications
- ✅ Enterprises needing observable, reliable caching
- ✅ Developers frustrated with StackExchange.Redis allocations/complexity
- ✅ Cloud-native apps using .NET Aspire

**Non-Target Users:**
- ❌ Users needing full Redis command surface (use StackExchange.Redis)
- ❌ Users needing Pub/Sub as primary pattern (use message broker)
- ❌ Users needing complete cluster orchestration for all Redis workloads (use StackExchange.Redis)

## Hybrid Approach (Recommended)

Use **VapeCache + StackExchange.Redis** together:

```csharp
// VapeCache for caching (fast, observable, reliable)
builder.Services.AddVapecacheRedisConnections();
builder.Services.AddVapecacheCaching();

// StackExchange.Redis for advanced Pub/Sub workflows (pattern subscriptions, broker semantics)
builder.Services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect("localhost:6379"));

// Use ICacheService for caching
var cache = serviceProvider.GetRequiredService<ICacheService>();
await cache.SetAsync("key", value, options, ct);

// Use IConnectionMultiplexer for advanced Pub/Sub
var redis = serviceProvider.GetRequiredService<IConnectionMultiplexer>();
await redis.GetSubscriber().SubscribeAsync("channel", handler);
```

**Benefits:**
- VapeCache handles caching (optimized for GET/SET performance)
- StackExchange.Redis handles advanced features (Pub/Sub, Lua, etc.)
- Both can share the same Redis instance
- Each library does what it's best at

## Decision Matrix

| Feature | VapeCache | StackExchange.Redis | Recommendation |
|---------|-----------|---------------------|----------------|
| GET/SET caching | ✅ Optimized | ✅ Supported | Use VapeCache |
| Hybrid cache | ✅ Built-in | ❌ Manual | Use VapeCache |
| Circuit breaker | ✅ Built-in | ❌ Manual | Use VapeCache |
| Stampede protection | ✅ Built-in | ❌ Manual | Use VapeCache |
| OpenTelemetry | ✅ Native | ⚠️ Manual | Use VapeCache |
| Pub/Sub | ⚠️ Basic channels | ✅ Native | Use VapeCache for simple channel pub/sub; use SER for advanced patterns |
| Lua scripting | ❌ Not supported | ✅ Native | Use SER |
| Streams | ❌ Not supported | ✅ Native | Use SER |
| Cluster mode (cache-path redirects) | ✅ Partial | ✅ Native | Use VapeCache for cache path, SER for full orchestration |
| 200+ commands | ❌ 20 commands | ✅ Full surface | Use SER |

## Frequently Asked Questions

### Q: Why not just contribute to StackExchange.Redis?
**A:** Different design goals. SER optimizes for API completeness (200+ commands). VapeCache optimizes for caching performance, observability, and reliability. Both are valid approaches.

### Q: Can I use VapeCache and StackExchange.Redis together?
**A:** Yes! This is the recommended approach. Use VapeCache for caching, SER for advanced features.

### Q: Does VapeCache support Redis cluster?
**A:** Partially. Core cache-path commands handle MOVED/ASK redirects. Full cluster orchestration across every command is still out of scope.

### Q: How much Pub/Sub is supported?
**A:** Basic channel pub/sub is available now through `IRedisPubSubService` (via `VapeCache.Extensions.PubSub`). Advanced pattern/pubsub-broker scenarios remain out of scope.

### Q: What if I need a command that's not implemented?
**A:** Three options:
1. Use StackExchange.Redis for that command (hybrid approach)
2. Request feature in [GitHub Issues](https://github.com/haxxornulled/VapeCache/issues)
3. Implement it yourself and contribute a PR

### Q: Is VapeCache production-ready?
**A:** Yes, for caching use cases. Basic pub/sub channels are supported; Lua/Streams remain out of scope; cluster support is partial and cache-path focused.

## References

- [Redis Protocol Support](REDIS_PROTOCOL_SUPPORT.md) - Detailed command support matrix
- [OSS vs Enterprise](OSS_VS_ENTERPRISE.md) - Product boundary and scope
- [GitHub Issues](https://github.com/haxxornulled/VapeCache/issues) - Feature requests and bug reports
