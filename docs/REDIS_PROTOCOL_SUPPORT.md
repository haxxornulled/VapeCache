# Redis Protocol Support

## Overview

VapeCache implements the **RESP2 (Redis Serialization Protocol v2)** baseline. This document explicitly states what Redis features are supported, what's planned, and what's intentionally excluded.

## Supported Commands (RESP2 Baseline)

### ✅ String Commands
| Command | Status | API Method |
|---------|--------|------------|
| GET | ✅ Supported | `IRedisCommandExecutor.GetAsync()` |
| SET | ✅ Supported | `IRedisCommandExecutor.SetAsync()` |
| MGET | ✅ Supported | `IRedisCommandExecutor.MGetAsync()` |
| MSET | ✅ Supported | `IRedisCommandExecutor.MSetAsync()` |
| GETEX | ✅ Supported | `IRedisCommandExecutor.GetExAsync()` |
| INCR | ✅ Supported | `IRedisCommandExecutor.IncrAsync()` |
| DECR | ✅ Supported | `IRedisCommandExecutor.DecrAsync()` |
| APPEND | 📋 Planned | API expansion Phase 1 |
| GETRANGE | 📋 Planned | API expansion Phase 1 |
| SETRANGE | 📋 Planned | API expansion Phase 1 |

### ✅ Hash Commands
| Command | Status | API Method |
|---------|--------|------------|
| HGET | ✅ Supported | `IRedisCommandExecutor.HGetAsync()` |
| HSET | ✅ Supported | `IRedisCommandExecutor.HSetAsync()` |
| HMGET | ✅ Supported | `IRedisCommandExecutor.HMGetAsync()` |
| HMSET | ✅ Supported | `IRedisCommandExecutor.HMSetAsync()` |
| HDEL | ✅ Supported | `IRedisCommandExecutor.HDelAsync()` |
| HEXISTS | ✅ Supported | `IRedisCommandExecutor.HExistsAsync()` |
| HLEN | ✅ Supported | `IRedisCommandExecutor.HLenAsync()` |
| HGETALL | 📋 Planned | API expansion Phase 1 |
| HKEYS | 📋 Planned | API expansion Phase 1 |
| HVALS | 📋 Planned | API expansion Phase 1 |

### ✅ List Commands
| Command | Status | API Method |
|---------|--------|------------|
| LPUSH | ✅ Supported | `IRedisCommandExecutor.LPushAsync()` |
| RPUSH | ✅ Supported | `IRedisCommandExecutor.RPushAsync()` |
| LPOP | ✅ Supported | `IRedisCommandExecutor.LPopAsync()` |
| RPOP | 📋 Planned | API expansion Phase 1 |
| LLEN | ✅ Supported | `IRedisCommandExecutor.LLenAsync()` |
| LRANGE | 📋 Planned | API expansion Phase 1 |
| LTRIM | 📋 Planned | API expansion Phase 1 |
| LINDEX | 📋 Planned | API expansion Phase 1 |

### ✅ Set Commands
| Command | Status | API Method |
|---------|--------|------------|
| SADD | ✅ Supported | `IRedisCommandExecutor.SAddAsync()` |
| SREM | ✅ Supported | `IRedisCommandExecutor.SRemAsync()` |
| SISMEMBER | ✅ Supported | `IRedisCommandExecutor.SIsMemberAsync()` |
| SCARD | ✅ Supported | `IRedisCommandExecutor.SCardAsync()` |
| SMEMBERS | 📋 Planned | API expansion Phase 1 |
| SUNION | 📋 Planned | API expansion Phase 1 |
| SINTER | 📋 Planned | API expansion Phase 1 |
| SDIFF | 📋 Planned | API expansion Phase 1 |

### ✅ Sorted Set Commands
| Command | Status | API Method |
|---------|--------|------------|
| ZADD | ✅ Supported | `IRedisCommandExecutor.ZAddAsync()` |
| ZREM | ✅ Supported | `IRedisCommandExecutor.ZRemAsync()` |
| ZSCORE | ✅ Supported | `IRedisCommandExecutor.ZScoreAsync()` |
| ZCARD | ✅ Supported | `IRedisCommandExecutor.ZCardAsync()` |
| ZRANGE | 📋 Planned | API expansion Phase 1 |
| ZRANK | 📋 Planned | API expansion Phase 1 |
| ZINCRBY | 📋 Planned | API expansion Phase 1 |

### ✅ Key Commands
| Command | Status | API Method |
|---------|--------|------------|
| DEL | ✅ Supported | `IRedisCommandExecutor.DelAsync()` |
| EXISTS | ✅ Supported | `IRedisCommandExecutor.ExistsAsync()` |
| EXPIRE | ✅ Supported | `IRedisCommandExecutor.ExpireAsync()` |
| TTL | ✅ Supported | `IRedisCommandExecutor.TtlAsync()` |
| RENAME | 📋 Planned | API expansion Phase 1 |
| PERSIST | 📋 Planned | API expansion Phase 1 |

### ✅ Connection Commands
| Command | Status | API Method |
|---------|--------|------------|
| PING | ✅ Supported | `IRedisCommandExecutor.PingAsync()` |
| AUTH | ✅ Supported | Internal (connection setup) |
| SELECT | ✅ Supported | Internal (connection setup) |
| QUIT | ✅ Supported | Internal (connection disposal) |

### ✅ Server Commands
| Command | Status | API Method |
|---------|--------|------------|
| INFO | 📋 Planned | API expansion Phase 1 |
| CONFIG GET | 📋 Planned | API expansion Phase 1 |
| DBSIZE | 📋 Planned | API expansion Phase 1 |
| FLUSHDB | 📋 Planned | API expansion Phase 1 |
| FLUSHALL | 📋 Planned | API expansion Phase 1 |

## Not Supported (Intentional Exclusions)

### ❌ Lua Scripting
| Command | Status | Reason |
|---------|--------|--------|
| EVAL | ❌ Not supported | Complex API surface, low ROI for baseline |
| EVALSHA | ❌ Not supported | Requires script management |
| SCRIPT LOAD | ❌ Not supported | Requires script management |

**Planned:** API expansion Phase 2 (Q2 2025)

### ❌ Pub/Sub
| Command | Status | Reason |
|---------|--------|--------|
| SUBSCRIBE | ❌ Not supported | Requires dedicated connection per subscriber |
| PSUBSCRIBE | ❌ Not supported | Pattern matching adds complexity |
| PUBLISH | ❌ Not supported | Requires different architecture (fire-and-forget) |
| UNSUBSCRIBE | ❌ Not supported | Pub/Sub not supported |

**Planned:** API expansion Phase 2 (Q2 2025)

**Workaround:** Use StackExchange.Redis for Pub/Sub alongside VapeCache for caching.

### ❌ Streams
| Command | Status | Reason |
|---------|--------|--------|
| XADD | ❌ Not supported | Streams require RESP3 for optimal performance |
| XREAD | ❌ Not supported | Blocking reads require dedicated connection |
| XGROUP | ❌ Not supported | Consumer groups require complex state management |

**Planned:** Future consideration (depends on RESP3 adoption)

### ❌ Transactions
| Command | Status | Reason |
|---------|--------|--------|
| MULTI | ❌ Not supported | Transactions require different API shape |
| EXEC | ❌ Not supported | Conflicts with pipelined multiplexing |
| WATCH | ❌ Not supported | Optimistic locking requires dedicated connection |

**Planned:** API expansion Phase 2 (Q2 2025) - Batch API with optimistic execution

### ❌ Cluster Mode
| Feature | Status | Reason |
|---------|--------|--------|
| MOVED redirects | ❌ Not supported | Requires cluster topology tracking |
| ASK redirects | ❌ Not supported | Requires cross-shard request routing |
| CLUSTER commands | ❌ Not supported | Baseline targets single-instance Redis |

**Planned:** Future consideration (depends on demand)

**Workaround:** Use Redis Sentinel for HA, or use cluster-aware client like StackExchange.Redis.

### ❌ RESP3 Protocol
| Feature | Status | Reason |
|---------|--------|--------|
| HELLO 3 | ❌ Not supported | VapeCache uses RESP2 only |
| Push messages | ❌ Not supported | Requires RESP3 |
| Client-side caching | ❌ Not supported | Requires RESP3 + push messages |

**Planned:** Future consideration (RESP2 is stable and widely supported)

## Protocol Behavior

### RESP2 Compliance
VapeCache implements RESP2 as specified in [Redis Protocol Specification](https://redis.io/docs/reference/protocol-spec/).

**Supported reply types:**
- ✅ Simple strings (`+OK\r\n`)
- ✅ Errors (`-ERR unknown command\r\n`)
- ✅ Integers (`:42\r\n`)
- ✅ Bulk strings (`$5\r\nhello\r\n`)
- ✅ Arrays (`*2\r\n$3\r\nfoo\r\n$3\r\nbar\r\n`)
- ✅ Null bulk string (`$-1\r\n`)
- ✅ Null array (`*-1\r\n`)

**Not supported (RESP3 only):**
- ❌ Push messages
- ❌ Streamed strings/arrays
- ❌ Map/Set/Attribute types

### Pipelining Behavior
VapeCache uses **ordered multiplexing** - commands sent on the same connection complete in FIFO order.

**Guarantees:**
- ✅ Commands sent in order A → B → C will complete in order A → B → C
- ✅ No response reordering within a connection
- ✅ Thread-safe concurrent sends (internal queueing preserves order)

**Limitations:**
- ❌ No transaction isolation (use Redis MULTI/EXEC if needed)
- ❌ No atomic multi-key operations across commands (use Lua or transactions)

### Error Handling
VapeCache translates Redis errors to .NET exceptions:

| Redis Error | .NET Exception | Example |
|-------------|---------------|---------|
| `-WRONGTYPE` | `InvalidOperationException` | Operating on wrong value type |
| `-NOAUTH` | `InvalidOperationException` | Authentication failed |
| `-ERR unknown command` | `NotSupportedException` | Command not implemented |
| `-MOVED` / `-ASK` | `NotSupportedException` | Cluster not supported |
| Socket/network errors | `IOException` or `SocketException` | Connection lost |

## API Expansion Roadmap

See [docs/API_EXPANSION_PLAN.md](API_EXPANSION_PLAN.md) for detailed roadmap.

**Phase 1 (6 weeks):** Missing String, Hash, Set, Sorted Set, List, Key commands
**Phase 2 (2 weeks):** Batch/Transaction API, Pub/Sub, Lua scripting
**Phase 3 (4 weeks):** Fluent API builders, source generators
**Phase 4 (3 weeks):** Enterprise features (advanced telemetry, pooling)

## Migration from StackExchange.Redis

VapeCache is **not a drop-in replacement** for StackExchange.Redis. It's a focused, high-performance caching library.

**Use VapeCache for:**
- ✅ High-performance GET/SET caching
- ✅ Hybrid cache (Redis + in-memory fallback)
- ✅ Observable, predictable memory usage
- ✅ Enterprise hardening (circuit breaker, stampede protection)

**Use StackExchange.Redis for:**
- ❌ Pub/Sub
- ❌ Lua scripting
- ❌ Streams
- ❌ Cluster mode
- ❌ Full Redis command surface (200+ commands)

**Hybrid Approach (Recommended):**
```csharp
// VapeCache for caching (fast, observable, reliable)
services.AddVapecacheCaching();
services.AddSingleton<ICacheService>(sp => sp.GetRequiredService<HybridCacheService>());

// StackExchange.Redis for Pub/Sub (when needed)
services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect("localhost:6379"));
```

## References

- [Redis Commands Reference](https://redis.io/commands/)
- [RESP2 Protocol Specification](https://redis.io/docs/reference/protocol-spec/)
- [VapeCache API Expansion Plan](API_EXPANSION_PLAN.md)
- [VapeCache Non-Goals](NON_GOALS.md)
