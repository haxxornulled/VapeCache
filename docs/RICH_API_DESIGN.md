# VapeCache Rich API Design

**Date:** December 25, 2025
**Status:** 🚧 **DESIGN PHASE**

## Vision

Create an **amazing developer experience** for Redis caching that goes beyond simple GET/SET:
- Support Redis data structures (HASH, LIST, SET, SORTED SET)
- Detect and leverage RedisJSON module when available
- Provide typed, fluent APIs with compile-time safety
- Maintain VapeCache's performance and reliability guarantees

## Current State

### Existing Commands (Implemented ✅)

**String Operations:**
- `GetAsync` - GET key
- `GetExAsync` - GETEX key (with TTL refresh)
- `MGetAsync` - MGET key1 key2...
- `SetAsync` - SET key value [EX seconds]
- `MSetAsync` - MSET key1 value1 key2 value2...
- `DeleteAsync` - DEL key
- `UnlinkAsync` - UNLINK key (async delete)
- `TtlSecondsAsync` - TTL key
- `PTtlMillisecondsAsync` - PTTL key

**Hash Operations:**
- `HSetAsync` - HSET key field value
- `HGetAsync` - HGET key field
- `HMGetAsync` - HMGET key field1 field2...
- `HGetLeaseAsync` - HGET with zero-copy lease

**List Operations:**
- `LPushAsync` - LPUSH key value
- `LPopAsync` - LPOP key
- `LRangeAsync` - LRANGE key start stop
- `LPopLeaseAsync` - LPOP with zero-copy lease

### Proposed Additions (Not Yet Implemented)

**List Operations:**
- `RPushAsync` - RPUSH key value (push to tail)
- `RPopAsync` - RPOP key (pop from tail)
- `LLenAsync` - LLEN key (list length)

**Set Operations:**
- `SAddAsync` - SADD key member
- `SRemAsync` - SREM key member
- `SIsMemberAsync` - SISMEMBER key member
- `SMembersAsync` - SMEMBERS key
- `SCardAsync` - SCARD key (set cardinality)

**Server Commands:**
- `PingAsync` - PING (health check)
- `ModuleListAsync` - MODULE LIST (detect RedisJSON, RediSearch, etc.)

## Design Principles

### 1. Layered API Approach

**Low-Level:** `IRedisCommandExecutor` (already exists)
- Raw Redis commands
- `byte[]` / `ReadOnlyMemory<byte>` for maximum performance
- Zero allocations where possible (leases)
- Used by VapeCache internals

**Mid-Level:** `ICacheService` (already exists)
- Hybrid cache semantics (Redis + in-memory fallback)
- Circuit breaker protection
- Serialization helpers
- Used by most applications

**High-Level:** `IVapeCache<T>` (new!)
- Strongly-typed collections
- Fluent API
- RedisJSON support when available
- Delightful developer experience

### 2. Strongly-Typed Collections

**Problem:** Current API is untyped:
```csharp
// ❌ NOT IDEAL - no type safety
await cache.SetAsync("user:123", JsonSerializer.Serialize(user), ...);
var json = await cache.GetAsync("user:123");
var user = JsonSerializer.Deserialize<User>(json);
```

**Solution:** Typed collections:
```csharp
// ✅ AMAZING - compile-time safety
var users = cache.Hash<User>("users");
await users.SetAsync("123", user);
var user = await users.GetAsync("123");
```

### 3. Redis Data Structure APIs

#### Hash API (for storing objects by field)
```csharp
public interface ICacheHash<T>
{
    ValueTask<T?> GetAsync(string field, CancellationToken ct = default);
    ValueTask SetAsync(string field, T value, CancellationToken ct = default);
    ValueTask<T[]> GetManyAsync(string[] fields, CancellationToken ct = default);
    ValueTask<Dictionary<string, T>> GetAllAsync(CancellationToken ct = default);
    ValueTask<bool> ExistsAsync(string field, CancellationToken ct = default);
    ValueTask<long> DeleteAsync(string field, CancellationToken ct = default);
    ValueTask<long> CountAsync(CancellationToken ct = default);
}
```

**Use Case:**
```csharp
// Store user profiles by ID
var userProfiles = cache.Hash<UserProfile>("user:profiles");
await userProfiles.SetAsync("123", new UserProfile { Name = "Alice", Email = "alice@example.com" });
var alice = await userProfiles.GetAsync("123");

// Get multiple users in one command (HMGET)
var profiles = await userProfiles.GetManyAsync(new[] { "123", "456", "789" });
```

#### List API (for queues, timelines, logs)
```csharp
public interface ICacheList<T>
{
    ValueTask<long> PushAsync(T value, CancellationToken ct = default); // RPUSH (append)
    ValueTask<long> PushFrontAsync(T value, CancellationToken ct = default); // LPUSH (prepend)
    ValueTask<T?> PopAsync(CancellationToken ct = default); // LPOP (from front)
    ValueTask<T?> PopBackAsync(CancellationToken ct = default); // RPOP (from back)
    ValueTask<T[]> RangeAsync(long start, long stop, CancellationToken ct = default);
    ValueTask<long> LengthAsync(CancellationToken ct = default);
    ValueTask<T[]> GetAllAsync(CancellationToken ct = default); // LRANGE 0 -1
}
```

**Use Case:**
```csharp
// Activity feed (most recent first)
var feed = cache.List<ActivityEvent>("user:123:feed");
await feed.PushFrontAsync(new ActivityEvent { Type = "login", Timestamp = DateTimeOffset.Now });
var recentActivity = await feed.RangeAsync(0, 9); // Get 10 most recent

// Job queue (FIFO)
var jobs = cache.List<Job>("background:jobs");
await jobs.PushAsync(new Job { Type = "SendEmail", Payload = {...} });
var nextJob = await jobs.PopAsync(); // Get oldest job
```

#### Set API (for unique memberships, tags)
```csharp
public interface ICacheSet<T>
{
    ValueTask<bool> AddAsync(T member, CancellationToken ct = default);
    ValueTask<bool> RemoveAsync(T member, CancellationToken ct = default);
    ValueTask<bool> ContainsAsync(T member, CancellationToken ct = default);
    ValueTask<T[]> MembersAsync(CancellationToken ct = default);
    ValueTask<long> CountAsync(CancellationToken ct = default);
}
```

**Use Case:**
```csharp
// User tags
var tags = cache.Set<string>("user:123:tags");
await tags.AddAsync("premium");
await tags.AddAsync("verified");
var isPremium = await tags.ContainsAsync("premium"); // true

// Online users
var onlineUsers = cache.Set<Guid>("app:online_users");
await onlineUsers.AddAsync(userId);
var onlineCount = await onlineUsers.CountAsync();
```

### 4. RedisJSON Support (When Available)

**Detection:**
```csharp
var modules = await redis.ModuleListAsync(ct);
var hasRedisJson = modules.Contains("ReJSON");
```

**Enhanced JSON Commands (if RedisJSON available):**
- `JSON.SET` - Store JSON directly (no serialization overhead)
- `JSON.GET` - Retrieve JSON with JSONPath queries
- `JSON.MGET` - Get JSON fields from multiple keys
- `JSON.TYPE` - Get type of JSON element
- `JSON.NUMINCRBY` - Increment numeric field atomically

**API Design:**
```csharp
public interface IJsonCache
{
    bool IsAvailable { get; } // RedisJSON module detected

    // If RedisJSON available: use JSON.* commands
    // If not available: fallback to GET/SET with serialization
    ValueTask<T?> GetAsync<T>(string key, string? path = null, CancellationToken ct = default);
    ValueTask SetAsync<T>(string key, T value, string? path = null, CancellationToken ct = default);
}
```

**Automatic Fallback:**
```csharp
var jsonCache = cache.Json();

// If RedisJSON is installed:
//   → Uses JSON.SET user:123 . '{"name":"Alice"}'
// If RedisJSON is NOT installed:
//   → Uses SET user:123 (serialized JSON bytes)
await jsonCache.SetAsync("user:123", user);
```

## Implementation Plan

### Phase 1: Complete Core Commands ⚡ **PRIORITY**
1. Implement missing List commands (RPUSH, RPOP, LLEN)
2. Implement Set commands (SADD, SREM, SISMEMBER, SMEMBERS, SCARD)
3. Implement PING for health checks
4. Implement MODULE LIST for capability detection

**Estimated Effort:** 4-6 hours (straightforward RESP encoding)

### Phase 2: Typed Collection APIs 🎯 **HIGH VALUE**
1. Design `ICacheHash<T>`, `ICacheList<T>`, `ICacheSet<T>` interfaces
2. Implement adapters over `IRedisCommandExecutor`
3. Wire up serialization (use existing `ICacheCodecProvider`)
4. Add factory methods to `IVapeCache`

**API Surface:**
```csharp
public interface IVapeCache
{
    // Existing
    ValueTask<T?> GetAsync<T>(string key, CancellationToken ct = default);
    ValueTask SetAsync<T>(string key, T value, CacheEntryOptions options, CancellationToken ct = default);

    // NEW - Typed collections
    ICacheHash<T> Hash<T>(string key);
    ICacheList<T> List<T>(string key);
    ICacheSet<T> Set<T>(string key);
}
```

**Estimated Effort:** 8-12 hours (design + implementation + tests)

### Phase 3: RedisJSON Integration 🚀 **ADVANCED**
1. Implement MODULE LIST command
2. Detect RedisJSON on startup (cache result)
3. Implement JSON.* commands in RedisCommandExecutor
4. Create `IJsonCache` with automatic fallback
5. Add JSONPath query support

**Estimated Effort:** 12-16 hours (complex protocol, fallback logic, testing)

### Phase 4: Documentation & Examples 📚
1. Update API reference docs
2. Create usage examples for each data structure
3. Add RedisJSON migration guide
4. Update README with new capabilities

**Estimated Effort:** 4-6 hours

## Questions for Decision

### 1. Naming Convention
**Option A:** Redis-style (current)
```csharp
await cache.HSetAsync("users", "123", user);
await cache.LPushAsync("feed", event);
```

**Option B:** .NET-style (proposed)
```csharp
await cache.Hash<User>("users").SetAsync("123", user);
await cache.List<Event>("feed").PushFrontAsync(event);
```

**Recommendation:** **Option B** - More discoverable, better IntelliSense

### 2. RedisJSON Fallback Behavior
**Option A:** Explicit API (user knows which they're using)
```csharp
if (cache.Json.IsAvailable)
    await cache.Json.SetAsync("user:123", user);
else
    await cache.SetAsync("user:123", user);
```

**Option B:** Transparent fallback (automatic)
```csharp
// Uses RedisJSON if available, standard GET/SET if not
await cache.Json().SetAsync("user:123", user);
```

**Recommendation:** **Option B** - Simpler for users, graceful degradation

### 3. Performance vs. Convenience
**Option A:** Low-level only (maximum performance)
- Keep `IRedisCommandExecutor` as the only API
- Users write their own wrappers

**Option B:** Layered approach (both)
- Low-level `IRedisCommandExecutor` for advanced users
- High-level `IVapeCache.Hash<T>()` for productivity

**Recommendation:** **Option B** - Best of both worlds

## Success Criteria

An "amazing API" should:
1. ✅ **Be discoverable** - IntelliSense guides users to the right method
2. ✅ **Be type-safe** - Compile-time errors for invalid operations
3. ✅ **Be consistent** - Patterns work the same across data structures
4. ✅ **Be performant** - No unnecessary allocations or serialization
5. ✅ **Be resilient** - Circuit breaker + fallback work for all operations
6. ✅ **Be documented** - Every method has clear examples

## Example: Before vs. After

### Before (Current API)
```csharp
// Store user profile
var json = JsonSerializer.Serialize(user);
var bytes = Encoding.UTF8.GetBytes(json);
await cache.SetAsync("user:123", bytes, new CacheEntryOptions { ... });

// Retrieve user profile
var storedBytes = await cache.GetAsync("user:123");
if (storedBytes != null)
{
    var storedJson = Encoding.UTF8.GetString(storedBytes);
    var user = JsonSerializer.Deserialize<User>(storedJson);
}
```

### After (Proposed API)
```csharp
// Store user profile
await cache.SetAsync("user:123", user, ttl: TimeSpan.FromMinutes(5));

// Retrieve user profile
var user = await cache.GetAsync<User>("user:123");

// Use hash for multiple users
var users = cache.Hash<User>("users");
await users.SetAsync("123", user);
var alice = await users.GetAsync("123");

// Use list for activity feed
var feed = cache.List<ActivityEvent>("user:123:feed");
await feed.PushFrontAsync(new ActivityEvent { ... });
var recent = await feed.RangeAsync(0, 9);
```

**Developer Experience:** 10x better! 🚀

## Next Steps

1. **Get Approval** - Review this design doc
2. **Phase 1** - Implement missing core commands (quick win)
3. **Phase 2** - Build typed collection APIs (high value)
4. **Phase 3** - Add RedisJSON detection + fallback
5. **Phase 4** - Document everything

---

**Feedback Needed:**
- Which phases should we prioritize?
- Any API design preferences?
- Should we support Sorted Sets (ZADD, ZRANGE, etc.)?
- Any other Redis features developers need?
