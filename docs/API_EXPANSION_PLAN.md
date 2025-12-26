# VapeCache API Expansion Plan
## Making VapeCache 100x Better Than StackExchange.Redis

**Goal**: Create the most ergonomic, performant, and comprehensive Redis client API in .NET

**Status**: Planning Phase
**Target**: Q1 2026 Release

---

## Current State Analysis

### What We Have (VapeCache Today)

**RedisCommandExecutor** - Low-level command interface:
- ✅ String: GET, GETEX, SET, MGET, MSET
- ✅ Hash: HGET, HSET, HMGET
- ✅ List: LPUSH, LPOP, LRANGE
- ✅ Key: DEL, UNLINK, TTL, PTTL
- ✅ Zero-copy leasing: GetLeaseAsync, HGetLeaseAsync, LPopLeaseAsync
- ✅ Coalesced socket writes (5-30% faster)
- ✅ ArrayPool integration (70% fewer allocations)

**IVapeCache** - High-level typed interface:
- ✅ Type-safe `CacheKey<T>` with codec providers
- ✅ `GetOrCreateAsync` with stampede protection
- ✅ Region-based organization
- ✅ Zero-allocation hot paths

**Total Commands**: ~20
**StackExchange.Redis Commands**: ~200+

### What They Have (StackExchange.Redis)

**Command Coverage**:
- ❌ String: APPEND, GETDEL, GETSET, INCR, DECR, INCRBY, DECRBY, STRLEN, SETRANGE, GETRANGE
- ❌ Set: SADD, SMEMBERS, SISMEMBER, SCARD, SREM, SPOP, SRANDMEMBER, SINTER, SUNION, SDIFF
- ❌ Sorted Set: ZADD, ZRANGE, ZCARD, ZSCORE, ZRANK, ZREM, ZINCRBY, ZCOUNT, ZINTERSTORE, ZUNIONSTORE
- ❌ Hash: HINCRBY, HEXISTS, HDEL, HLEN, HKEYS, HVALS, HSCAN
- ❌ Key: EXISTS, EXPIRE, EXPIREAT, PERSIST, RENAME, TYPE, SCAN
- ❌ Geo: GEOADD, GEODIST, GEORADIUS, GEOSEARCH
- ❌ Stream: XADD, XREAD, XRANGE, XACK, XPENDING
- ❌ Pub/Sub: PUBLISH, SUBSCRIBE, PSUBSCRIBE
- ❌ Scripting: EVAL, EVALSHA, SCRIPT LOAD
- ❌ Transaction: MULTI, EXEC, WATCH, UNWATCH
- ❌ Batch: Pipelined operations

**API Surface**:
- ❌ ~200+ Redis command methods
- ❌ Batch/transaction support
- ❌ Pub/Sub channels
- ❌ Lua script execution
- ❌ Geo spatial queries
- ❌ Stream processing

---

## The VapeCache Advantage

### What Makes Us Better (Already)

1. **Performance**: 22% faster on small payloads, 10-15% faster overall
2. **Memory Efficiency**: 70% fewer allocations via ArrayPool + zero-copy leasing
3. **Type Safety**: `CacheKey<T>` prevents string-key mistakes
4. **Stampede Protection**: Built-in cache stampede mitigation
5. **Modern API**: ValueTask, Span<T>, ReadOnlyMemory<T>, IBufferWriter<T>
6. **Coalesced Writes**: Batches commands automatically (StackExchange doesn't have this)
7. **Zero-Copy Leasing**: RedisValueLease for large values (StackExchange copies everything)

### What Will Make Us 100x Better (The Plan)

1. **Complete Command Coverage** (match or exceed StackExchange.Redis)
2. **Fluent API Builders** (better than StackExchange.Redis)
3. **Advanced Type Safety** (no string-based commands)
4. **Compile-Time Validation** (source generators for cache keys)
5. **Performance Monitoring** (built-in telemetry, not opt-in)
6. **Intelligent Batching** (automatic vs manual)
7. **Modern Patterns** (IAsyncEnumerable for streams, channels for pub/sub)

---

## Phase 1: Core Command Expansion (4-6 weeks)

### 1.1 String Commands (Week 1)

**New methods in `RedisCommandExecutor`**:

```csharp
// Atomic increment/decrement
ValueTask<long> IncrAsync(string key, CancellationToken ct);
ValueTask<long> DecrAsync(string key, CancellationToken ct);
ValueTask<long> IncrByAsync(string key, long increment, CancellationToken ct);
ValueTask<long> DecrByAsync(string key, long decrement, CancellationToken ct);
ValueTask<double> IncrByFloatAsync(string key, double increment, CancellationToken ct);

// String manipulation
ValueTask<long> AppendAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct);
ValueTask<long> StrLenAsync(string key, CancellationToken ct);
ValueTask<byte[]?> GetRangeAsync(string key, long start, long end, CancellationToken ct);
ValueTask SetRangeAsync(string key, long offset, ReadOnlyMemory<byte> value, CancellationToken ct);

// Get + modify atomically
ValueTask<byte[]?> GetDelAsync(string key, CancellationToken ct);
ValueTask<byte[]?> GetSetAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct);

// SET with options (NX/XX/GET)
ValueTask<bool> SetNXAsync(string key, ReadOnlyMemory<byte> value, TimeSpan? ttl, CancellationToken ct);
ValueTask<bool> SetXXAsync(string key, ReadOnlyMemory<byte> value, TimeSpan? ttl, CancellationToken ct);
ValueTask<byte[]?> SetGetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan? ttl, CancellationToken ct);
```

**Why Better Than StackExchange**:
- Zero-copy leasing variants for all GET operations
- Span-based APIs where applicable
- Automatic coalescing for bulk operations

---

### 1.2 Hash Commands (Week 1-2)

**New methods in `RedisCommandExecutor`**:

```csharp
// Increment/decrement hash fields
ValueTask<long> HIncrByAsync(string key, string field, long increment, CancellationToken ct);
ValueTask<double> HIncrByFloatAsync(string key, string field, double increment, CancellationToken ct);

// Hash field existence and deletion
ValueTask<bool> HExistsAsync(string key, string field, CancellationToken ct);
ValueTask<long> HDelAsync(string key, string field, CancellationToken ct);
ValueTask<long> HDelAsync(string key, string[] fields, CancellationToken ct);

// Hash metadata
ValueTask<long> HLenAsync(string key, CancellationToken ct);
ValueTask<string[]> HKeysAsync(string key, CancellationToken ct);
ValueTask<byte[]?[]> HValsAsync(string key, CancellationToken ct);
ValueTask<(string Field, byte[] Value)[]> HGetAllAsync(string key, CancellationToken ct);

// Hash field length
ValueTask<long> HStrLenAsync(string key, string field, CancellationToken ct);

// Atomic multi-set
ValueTask HMSetAsync(string key, (string Field, ReadOnlyMemory<byte> Value)[] fields, CancellationToken ct);

// Scan (cursor-based iteration)
IAsyncEnumerable<(string Field, byte[] Value)> HScanAsync(string key, string? pattern = null, int pageSize = 10, CancellationToken ct = default);
```

**Why Better Than StackExchange**:
- `IAsyncEnumerable<T>` for HScan (modern async streaming)
- Zero-copy lease variants
- Tuple returns instead of `HashEntry[]` structs

---

### 1.3 Set Commands (Week 2)

**New methods in `RedisCommandExecutor`**:

```csharp
// Set operations
ValueTask<long> SAddAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct);
ValueTask<long> SAddAsync(string key, ReadOnlyMemory<byte>[] members, CancellationToken ct);
ValueTask<long> SRemAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct);
ValueTask<long> SRemAsync(string key, ReadOnlyMemory<byte>[] members, CancellationToken ct);

// Set queries
ValueTask<bool> SIsMemberAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct);
ValueTask<long> SCardAsync(string key, CancellationToken ct);
ValueTask<byte[]?[]> SMembersAsync(string key, CancellationToken ct);
ValueTask<byte[]?> SPopAsync(string key, CancellationToken ct);
ValueTask<byte[]?[]> SPopAsync(string key, long count, CancellationToken ct);
ValueTask<byte[]?> SRandMemberAsync(string key, CancellationToken ct);
ValueTask<byte[]?[]> SRandMemberAsync(string key, long count, CancellationToken ct);

// Set combinators
ValueTask<byte[]?[]> SInterAsync(string[] keys, CancellationToken ct);
ValueTask<byte[]?[]> SUnionAsync(string[] keys, CancellationToken ct);
ValueTask<byte[]?[]> SDiffAsync(string[] keys, CancellationToken ct);
ValueTask<long> SInterStoreAsync(string destination, string[] keys, CancellationToken ct);
ValueTask<long> SUnionStoreAsync(string destination, string[] keys, CancellationToken ct);
ValueTask<long> SDiffStoreAsync(string destination, string[] keys, CancellationToken ct);

// Set scanning
IAsyncEnumerable<byte[]> SScanAsync(string key, string? pattern = null, int pageSize = 10, CancellationToken ct = default);
```

**Why Better Than StackExchange**:
- `IAsyncEnumerable<T>` for SScan
- Overloads for single vs multiple members
- Span-based comparison for set membership checks

---

### 1.4 Sorted Set Commands (Week 3)

**New methods in `RedisCommandExecutor`**:

```csharp
// Sorted set add/remove
ValueTask<long> ZAddAsync(string key, double score, ReadOnlyMemory<byte> member, CancellationToken ct);
ValueTask<long> ZAddAsync(string key, (double Score, ReadOnlyMemory<byte> Member)[] members, CancellationToken ct);
ValueTask<long> ZRemAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct);
ValueTask<long> ZRemAsync(string key, ReadOnlyMemory<byte>[] members, CancellationToken ct);

// Sorted set queries
ValueTask<long> ZCardAsync(string key, CancellationToken ct);
ValueTask<double?> ZScoreAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct);
ValueTask<long?> ZRankAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct);
ValueTask<long?> ZRevRankAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct);

// Sorted set ranges
ValueTask<byte[]?[]> ZRangeAsync(string key, long start, long stop, CancellationToken ct);
ValueTask<(byte[] Member, double Score)[]> ZRangeWithScoresAsync(string key, long start, long stop, CancellationToken ct);
ValueTask<byte[]?[]> ZRevRangeAsync(string key, long start, long stop, CancellationToken ct);
ValueTask<(byte[] Member, double Score)[]> ZRevRangeWithScoresAsync(string key, long start, long stop, CancellationToken ct);

// Score-based ranges
ValueTask<byte[]?[]> ZRangeByScoreAsync(string key, double min, double max, long? skip = null, long? take = null, CancellationToken ct = default);
ValueTask<(byte[] Member, double Score)[]> ZRangeByScoreWithScoresAsync(string key, double min, double max, long? skip = null, long? take = null, CancellationToken ct = default);
ValueTask<byte[]?[]> ZRevRangeByScoreAsync(string key, double max, double min, long? skip = null, long? take = null, CancellationToken ct = default);

// Sorted set operations
ValueTask<long> ZIncrByAsync(string key, double increment, ReadOnlyMemory<byte> member, CancellationToken ct);
ValueTask<long> ZCountAsync(string key, double min, double max, CancellationToken ct);
ValueTask<long> ZRemRangeByRankAsync(string key, long start, long stop, CancellationToken ct);
ValueTask<long> ZRemRangeByScoreAsync(string key, double min, double max, CancellationToken ct);

// Sorted set combinators
ValueTask<long> ZInterStoreAsync(string destination, string[] keys, double[]? weights = null, CancellationToken ct = default);
ValueTask<long> ZUnionStoreAsync(string destination, string[] keys, double[]? weights = null, CancellationToken ct = default);

// Scanning
IAsyncEnumerable<(byte[] Member, double Score)> ZScanAsync(string key, string? pattern = null, int pageSize = 10, CancellationToken ct = default);
```

**Why Better Than StackExchange**:
- Modern tuple returns: `(byte[] Member, double Score)[]`
- `IAsyncEnumerable<T>` for ZScan
- Nullable return types for missing members

---

### 1.5 Key Management Commands (Week 3)

**New methods in `RedisCommandExecutor`**:

```csharp
// Key existence and type
ValueTask<bool> ExistsAsync(string key, CancellationToken ct);
ValueTask<long> ExistsAsync(string[] keys, CancellationToken ct);
ValueTask<string> TypeAsync(string key, CancellationToken ct);

// Expiration management
ValueTask<bool> ExpireAsync(string key, TimeSpan ttl, CancellationToken ct);
ValueTask<bool> ExpireAtAsync(string key, DateTimeOffset expireAt, CancellationToken ct);
ValueTask<bool> PExpireAsync(string key, TimeSpan ttl, CancellationToken ct);
ValueTask<bool> PExpireAtAsync(string key, DateTimeOffset expireAt, CancellationToken ct);
ValueTask<bool> PersistAsync(string key, CancellationToken ct);

// Key renaming
ValueTask RenameAsync(string key, string newKey, CancellationToken ct);
ValueTask<bool> RenameNXAsync(string key, string newKey, CancellationToken ct);

// Key scanning
IAsyncEnumerable<string> ScanAsync(string? pattern = null, int pageSize = 10, CancellationToken ct = default);

// Random key
ValueTask<string?> RandomKeyAsync(CancellationToken ct);
```

**Why Better Than StackExchange**:
- `IAsyncEnumerable<string>` for SCAN
- `DateTimeOffset` for absolute expiration
- Consistent naming (ExpireAsync vs ExpireAtAsync)

---

## Phase 2: Advanced Features (4-6 weeks)

### 2.1 List Commands (Week 4)

```csharp
// List operations
ValueTask<long> RPushAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct);
ValueTask<byte[]?> RPopAsync(string key, CancellationToken ct);
ValueTask<byte[]?> LIndexAsync(string key, long index, CancellationToken ct);
ValueTask LSetAsync(string key, long index, ReadOnlyMemory<byte> value, CancellationToken ct);
ValueTask<long> LLenAsync(string key, CancellationToken ct);
ValueTask<long> LRemAsync(string key, long count, ReadOnlyMemory<byte> value, CancellationToken ct);
ValueTask LTrimAsync(string key, long start, long stop, CancellationToken ct);

// Blocking list operations
ValueTask<(string Key, byte[] Value)?> BLPopAsync(string[] keys, TimeSpan timeout, CancellationToken ct);
ValueTask<(string Key, byte[] Value)?> BRPopAsync(string[] keys, TimeSpan timeout, CancellationToken ct);
```

---

### 2.2 Batch/Transaction Support (Week 4-5)

**New interfaces**:

```csharp
public interface IRedisBatch : IAsyncDisposable
{
    // All IRedisCommandExecutor methods available
    // Returns ValueTask<T> that completes when batch is executed

    ValueTask ExecuteAsync(CancellationToken ct);
}

public interface IRedisTransaction : IAsyncDisposable
{
    // All IRedisCommandExecutor methods available
    // Returns ValueTask<T> that completes when transaction commits

    ValueTask<bool> CommitAsync(CancellationToken ct);
    ValueTask AbortAsync(CancellationToken ct);
}

// Usage
public interface IRedisCommandExecutor
{
    IRedisBatch CreateBatch();
    IRedisTransaction CreateTransaction();
}
```

**Example usage**:

```csharp
// Batch (pipelined operations)
await using var batch = executor.CreateBatch();
var task1 = batch.GetAsync("key1", ct);
var task2 = batch.SetAsync("key2", value, null, ct);
var task3 = batch.IncrAsync("counter", ct);
await batch.ExecuteAsync(ct); // Send all at once
var result1 = await task1;
var result2 = await task2;
var result3 = await task3;

// Transaction (atomic MULTI/EXEC)
await using var tx = executor.CreateTransaction();
var task1 = tx.IncrAsync("account:1:balance", ct);
var task2 = tx.DecrAsync("account:2:balance", ct);
var committed = await tx.CommitAsync(ct);
if (committed)
{
    var newBalance1 = await task1;
    var newBalance2 = await task2;
}
```

**Why Better Than StackExchange**:
- `IRedisBatch` and `IRedisTransaction` are disposable (RAII pattern)
- Compile-time safety: can't forget to execute
- Modern async patterns throughout

---

### 2.3 Pub/Sub (Week 5)

**New interface**:

```csharp
public interface IRedisPubSub : IAsyncDisposable
{
    ValueTask PublishAsync(string channel, ReadOnlyMemory<byte> message, CancellationToken ct);
    IAsyncEnumerable<RedisMessage> SubscribeAsync(string channel, CancellationToken ct);
    IAsyncEnumerable<RedisMessage> SubscribeAsync(string[] channels, CancellationToken ct);
    IAsyncEnumerable<RedisMessage> PSubscribeAsync(string pattern, CancellationToken ct);
}

public readonly record struct RedisMessage(string Channel, byte[] Data, bool IsPattern);
```

**Example usage**:

```csharp
await using var pubsub = executor.CreatePubSub();

// Subscribe
await foreach (var msg in pubsub.SubscribeAsync("notifications", ct))
{
    Console.WriteLine($"Received: {Encoding.UTF8.GetString(msg.Data)}");
}

// Publish
await pubsub.PublishAsync("notifications", Encoding.UTF8.GetBytes("Hello"), ct);
```

**Why Better Than StackExchange**:
- `IAsyncEnumerable<RedisMessage>` instead of event callbacks
- Channel pattern matching for async streams
- Modern cancellation token support

---

### 2.4 Lua Scripting (Week 6)

**New interface**:

```csharp
public interface IRedisScripting
{
    ValueTask<RedisValueLease> EvalAsync(string script, string[] keys, ReadOnlyMemory<byte>[] args, CancellationToken ct);
    ValueTask<string> ScriptLoadAsync(string script, CancellationToken ct);
    ValueTask<RedisValueLease> EvalShaAsync(string sha1, string[] keys, ReadOnlyMemory<byte>[] args, CancellationToken ct);
    ValueTask<bool> ScriptExistsAsync(string sha1, CancellationToken ct);
    ValueTask ScriptFlushAsync(CancellationToken ct);
}
```

**Example usage**:

```csharp
var scripting = executor.Scripting;

// Atomic rate limiting script
var script = @"
    local current = redis.call('incr', KEYS[1])
    if current == 1 then
        redis.call('expire', KEYS[1], ARGV[1])
    end
    return current
";

var result = await scripting.EvalAsync(script, new[] { "ratelimit:user:123" }, new[] { Encoding.UTF8.GetBytes("60") }, ct);
var currentCount = result.ToInt64();
```

**Why Better Than StackExchange**:
- Separated scripting interface (clean API surface)
- Zero-copy lease for script results
- Script caching with SHA1 built-in

---

## Phase 3: Ergonomic API (4-6 weeks)

### 3.1 Fluent Builders (Week 7-8)

**Goal**: Make complex operations feel natural

```csharp
// Sorted set query builder
var users = await redis.SortedSet("leaderboard")
    .RangeByScore(min: 1000, max: 5000)
    .WithScores()
    .Skip(10)
    .Take(100)
    .OrderByDescending()
    .ToArrayAsync(ct);

// Hash builder
await redis.Hash("user:123")
    .Set("name", userName)
    .Set("email", email)
    .IncrBy("loginCount", 1)
    .ExpireIn(TimeSpan.FromHours(24))
    .ExecuteAsync(ct);

// Transaction builder
var success = await redis.Transaction()
    .DecrBy("inventory:item:42", quantity)
    .IncrBy("user:123:balance", -cost)
    .Append("user:123:purchases", purchaseRecord)
    .CommitAsync(ct);
```

**Implementation**:

```csharp
public interface IRedisFluentBuilder
{
    ISortedSetBuilder SortedSet(string key);
    IHashBuilder Hash(string key);
    ISetBuilder Set(string key);
    IListBuilder List(string key);
    ITransactionBuilder Transaction();
}

public interface ISortedSetBuilder
{
    ISortedSetRangeBuilder RangeByScore(double min, double max);
    ISortedSetRangeBuilder RangeByRank(long start, long stop);
    ValueTask<long> AddAsync(double score, ReadOnlyMemory<byte> member, CancellationToken ct);
    ValueTask<long> RemoveAsync(ReadOnlyMemory<byte> member, CancellationToken ct);
    // ... etc
}

public interface ISortedSetRangeBuilder
{
    ISortedSetRangeBuilder WithScores();
    ISortedSetRangeBuilder Skip(long count);
    ISortedSetRangeBuilder Take(long count);
    ISortedSetRangeBuilder OrderByDescending();
    ValueTask<byte[][]> ToArrayAsync(CancellationToken ct);
    ValueTask<(byte[] Member, double Score)[]> ToArrayWithScoresAsync(CancellationToken ct);
}
```

**Why Better Than StackExchange**:
- Fluent API feels like LINQ
- Compile-time safety (can't call WithScores() on wrong builder)
- Method chaining reduces errors

---

### 3.2 Source Generator for Type-Safe Keys (Week 8-9)

**Goal**: Catch cache key typos at compile time

```csharp
// Developer writes this
[CacheRegion]
public partial class UserCache
{
    [CacheKey] public static readonly CacheKey<User> ById;
    [CacheKey] public static readonly CacheKey<string> ByEmail;
    [CacheKey] public static readonly CacheKey<UserSession> Session;
}

// Source generator creates this
public partial class UserCache
{
    public static readonly CacheKey<User> ById = new("user:byId");
    public static readonly CacheKey<string> ByEmail = new("user:byEmail");
    public static readonly CacheKey<UserSession> Session = new("user:session");

    public static partial class Region
    {
        public static ICacheRegion Instance { get; } = VapeCache.Region("user");

        public static ValueTask<User?> GetByIdAsync(string id, CancellationToken ct) =>
            Instance.GetAsync(ById.WithSuffix(id), ct);

        public static ValueTask SetByIdAsync(string id, User user, TimeSpan? ttl, CancellationToken ct) =>
            Instance.SetAsync(ById.WithSuffix(id), user, new CacheEntryOptions { Ttl = ttl }, ct);
    }
}

// Usage (compile-time safe!)
var user = await UserCache.Region.GetByIdAsync("123", ct);
await UserCache.Region.SetByIdAsync("123", newUser, TimeSpan.FromMinutes(30), ct);
```

**Why Better Than StackExchange**:
- No magic strings
- IntelliSense for cache keys
- Refactoring support (rename all uses)
- Generated helper methods reduce boilerplate

---

### 3.3 Typed Codecs with Performance (Week 9-10)

**Goal**: Make serialization automatic but fast

```csharp
// Built-in codecs
services.AddVapeCache(options =>
{
    options.RegisterCodec<User>(JsonCodec.Default<User>()); // System.Text.Json
    options.RegisterCodec<Session>(MessagePackCodec.Default<Session>()); // MessagePack (faster)
    options.RegisterCodec<Guid>(GuidCodec.Instance); // Zero-allocation Guid codec
    options.RegisterCodec<int>(Int32Codec.Instance); // Zero-allocation int codec
});

// Custom codec
public class ProtobufCodec<T> : ICacheCodec<T> where T : IMessage<T>, new()
{
    public void Serialize(IBufferWriter<byte> writer, T value)
    {
        value.WriteTo(writer);
    }

    public T? Deserialize(ReadOnlySpan<byte> data)
    {
        var parser = new MessageParser<T>(() => new T());
        return parser.ParseFrom(data);
    }
}

// Usage
await cache.SetAsync(userKey, user, ct); // Automatically uses registered codec
var user = await cache.GetAsync(userKey, ct); // Automatically deserializes
```

**Built-in Codecs**:
- `JsonCodec<T>` - System.Text.Json (default)
- `MessagePackCodec<T>` - MessagePack (fastest)
- `ProtobufCodec<T>` - Google Protobuf
- `Utf8StringCodec` - Zero-allocation UTF-8 string
- `Int32Codec`, `Int64Codec`, `DoubleCodec` - Zero-allocation primitives
- `GuidCodec` - Zero-allocation GUID
- `DateTimeOffsetCodec` - Zero-allocation timestamp

**Why Better Than StackExchange**:
- StackExchange.Redis has no built-in serialization
- Developers must manually serialize/deserialize
- VapeCache makes it automatic but allows customization

---

## Phase 4: Enterprise Features (2-3 weeks)

### 4.1 Advanced Telemetry (Week 11)

**Built-in OpenTelemetry metrics**:
- `redis.commands.duration` - Histogram of command latencies
- `redis.commands.count` - Counter of commands by type
- `redis.commands.failures` - Counter of failed commands
- `redis.bytes.sent` - Counter of bytes sent
- `redis.bytes.received` - Counter of bytes received
- `redis.connections.active` - Gauge of active connections
- `redis.operations.in_flight` - Gauge of in-flight operations
- `redis.cache.hits` - Counter of cache hits
- `redis.cache.misses` - Counter of cache misses
- `redis.cache.stampede.coalesced` - Counter of coalesced stampede requests

**Built-in distributed tracing**:
- Every command creates an Activity with `db.system = redis`
- Command text logged as span attribute
- Error stack traces captured
- Connection info tagged

**Example**:
```csharp
services.AddVapeCache(options =>
{
    options.EnableTelemetry = true; // Default
    options.TelemetryOptions = new()
    {
        RecordCommandText = true, // Include command in traces
        RecordPayloadSize = true, // Include payload size in metrics
        SampleRate = 1.0 // Sample 100% of commands
    };
});
```

**Why Better Than StackExchange**:
- Built-in, not opt-in
- OpenTelemetry standard (Prometheus, Grafana, Jaeger compatible)
- Zero-allocation metrics (no boxing)

---

### 4.2 Connection Pooling Strategies (Week 11)

**Multiple pooling modes**:

```csharp
services.AddVapeCache(options =>
{
    options.ConnectionStrategy = ConnectionStrategy.RoundRobin; // Default
    // options.ConnectionStrategy = ConnectionStrategy.LeastLoaded;
    // options.ConnectionStrategy = ConnectionStrategy.Affinity;
});

public enum ConnectionStrategy
{
    RoundRobin,     // Simple round-robin (current)
    LeastLoaded,    // Choose connection with fewest in-flight ops
    Affinity        // Sticky connections per async context
}
```

**Why Better Than StackExchange**:
- StackExchange.Redis uses a single multiplexed connection
- VapeCache supports multiple connections for higher throughput
- Smart routing reduces head-of-line blocking

---

### 4.3 Resilience Policies (Week 12)

**Built-in retry and circuit breaker**:

```csharp
services.AddVapeCache(options =>
{
    options.Resilience = new()
    {
        RetryPolicy = new()
        {
            MaxRetries = 3,
            Backoff = TimeSpan.FromMilliseconds(100),
            BackoffMultiplier = 2.0
        },
        CircuitBreaker = new()
        {
            FailureThreshold = 10,
            SamplingDuration = TimeSpan.FromSeconds(30),
            BreakDuration = TimeSpan.FromSeconds(60)
        }
    };
});
```

**Why Better Than StackExchange**:
- Built-in, not external (Polly)
- Zero-allocation retry logic
- Integrated with telemetry

---

## API Comparison Matrix

| Feature | StackExchange.Redis | VapeCache (Current) | VapeCache (Planned) |
|---------|-------------------|-------------------|-------------------|
| **Command Coverage** | ~200+ commands | ~20 commands | ~200+ commands ✅ |
| **Performance** | Baseline | 10-30% faster | 10-30% faster ✅ |
| **Memory Efficiency** | Baseline | 70% fewer allocs | 70% fewer allocs ✅ |
| **Type Safety** | String keys | `CacheKey<T>` | Source-generated keys ✅ |
| **Serialization** | Manual | Codec providers | Auto + codecs ✅ |
| **Stampede Protection** | None | Built-in | Built-in ✅ |
| **Zero-Copy** | None | Leasing API | Leasing API ✅ |
| **Batching** | Manual | Automatic | Auto + manual ✅ |
| **Transactions** | Yes | ❌ | Yes ✅ |
| **Pub/Sub** | Events | ❌ | IAsyncEnumerable ✅ |
| **Scripting** | Yes | ❌ | Yes ✅ |
| **Fluent API** | No | No | Yes ✅ |
| **Telemetry** | Opt-in | Built-in | Enhanced ✅ |
| **Circuit Breaker** | External (Polly) | Basic | Advanced ✅ |
| **API Style** | IDatabase | IVapeCache | Multiple layers ✅ |

---

## Success Metrics

**Performance** (vs StackExchange.Redis):
- ✅ Small payloads: 20-30% faster (already achieved: 22%)
- ✅ Medium payloads: 10-15% faster (already achieved: 10-15%)
- ✅ Large payloads: 5-10% faster (already achieved: 5-7%)
- 🎯 Memory: 70%+ fewer allocations (already achieved)
- 🎯 Latency: P99 latency 20-40% lower under high load

**Developer Experience**:
- 🎯 100% feature parity with StackExchange.Redis
- 🎯 50% less boilerplate code in typical usage
- 🎯 Zero runtime cache key errors (compile-time validation)
- 🎯 IntelliSense for all cache operations

**Production Readiness**:
- 🎯 Built-in telemetry (OpenTelemetry standard)
- 🎯 Health checks for Kubernetes
- 🎯 Circuit breaker with automatic recovery
- 🎯 Connection pooling with smart routing

---

## Timeline

**Phase 1: Core Commands** (6 weeks)
- Week 1: String commands
- Week 2: Hash + Set commands
- Week 3: Sorted Set + Key commands
- Week 4: List commands
- Week 5: Batch/Transaction
- Week 6: Pub/Sub + Scripting

**Phase 2: Fluent API** (4 weeks)
- Week 7-8: Builder patterns
- Week 9-10: Source generators + codecs

**Phase 3: Enterprise** (3 weeks)
- Week 11: Telemetry + pooling
- Week 12: Resilience policies
- Week 13: Testing + documentation

**Total: ~13 weeks (Q1 2026)**

---

## Next Steps

1. ✅ Document current API surface
2. ✅ Research StackExchange.Redis command coverage
3. ✅ Create comprehensive expansion plan (this document)
4. 🎯 Begin Phase 1: String commands implementation
5. 🎯 Create benchmark suite for new commands
6. 🎯 Write API usage examples
7. 🎯 Update documentation

---

**References**:
- [StackExchange.Redis IDatabase Interface](https://github.com/StackExchange/StackExchange.Redis/blob/main/src/StackExchange.Redis/Interfaces/IDatabase.cs)
- [Basic Usage | StackExchange.Redis](https://stackexchange.github.io/StackExchange.Redis/Basics.html)
- [VapeCache COALESCED_WRITES.md](COALESCED_WRITES.md)
- [VapeCache PERFORMANCE_ANALYSIS.md](PERFORMANCE_ANALYSIS.md)

**Last Updated**: December 25, 2025
**Status**: Planning Complete, Ready for Implementation
