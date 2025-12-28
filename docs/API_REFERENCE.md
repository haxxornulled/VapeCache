# VapeCache API Reference

Complete reference documentation for VapeCache's public API.

## Table of Contents

- [Overview](#overview)
- [Core API](#core-api)
  - [ICacheService](#icacheservice)
  - [CacheEntryOptions](#cacheentryoptions)
- [Typed Collections API](#typed-collections-api)
  - [ICacheCollectionFactory](#icachecollectionfactory)
  - [ICacheList<T>](#icachelistt)
  - [ICacheSet<T>](#icachesett)
  - [ICacheHash<T>](#icachehasht)
- [Serialization](#serialization)
- [Error Handling](#error-handling)
- [Performance Patterns](#performance-patterns)
- [Examples](#examples)

---

## Overview

VapeCache provides three main APIs:

1. **Core API** ([ICacheService](#icacheservice)) - Low-level byte[] and zero-allocation operations
2. **Typed Collections API** - High-level Redis data structures with automatic serialization
3. **Circuit Breaker** - Automatic failover to in-memory mode (see [CIRCUIT_BREAKER.md](CIRCUIT_BREAKER.md))

**Key Features**:
- ✅ **Zero-allocation** patterns with `ReadOnlyMemory<byte>` and `IBufferWriter<byte>`
- ✅ **Automatic serialization** using `System.Text.Json` or custom codecs
- ✅ **Hybrid backend** - Seamless switching between Redis and in-memory
- ✅ **Circuit breaker** - Automatic failover on Redis unavailability
- ✅ **Connection pooling** - High-performance connection management
- ✅ **Result<T> pattern** - No exceptions for expected failures

---

## Core API

### ICacheService

The primary interface for cache operations. Supports both raw `byte[]` and typed data with custom serialization.

**Namespace**: `VapeCache.Abstractions.Caching`

#### Interface Definition

```csharp
public interface ICacheService
{
    string Name { get; }

    // Raw byte[] operations
    ValueTask<byte[]?> GetAsync(string key, CancellationToken ct);
    ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CacheEntryOptions options, CancellationToken ct);
    ValueTask<bool> RemoveAsync(string key, CancellationToken ct);

    // Zero-allocation typed operations
    ValueTask<T?> GetAsync<T>(string key, SpanDeserializer<T> deserialize, CancellationToken ct);
    ValueTask SetAsync<T>(string key, T value, Action<IBufferWriter<byte>, T> serialize, CacheEntryOptions options, CancellationToken ct);

    // Cache-aside pattern
    ValueTask<T> GetOrSetAsync<T>(
        string key,
        Func<CancellationToken, ValueTask<T>> factory,
        Action<IBufferWriter<byte>, T> serialize,
        SpanDeserializer<T> deserialize,
        CacheEntryOptions options,
        CancellationToken ct);
}
```

#### Delegates

```csharp
// Deserializer that works directly with ReadOnlySpan<byte> (zero-allocation)
public delegate T SpanDeserializer<T>(ReadOnlySpan<byte> span);

// Serializer that writes to IBufferWriter<byte> (zero-allocation)
// Action<IBufferWriter<byte>, T> serialize
```

---

### Methods

#### GetAsync (Raw Bytes)

Get raw cached bytes for a key.

```csharp
ValueTask<byte[]?> GetAsync(string key, CancellationToken ct);
```

**Parameters**:
- `key`: Cache key
- `ct`: Cancellation token

**Returns**: Cached bytes, or `null` if key doesn't exist

**Example**:
```csharp
var bytes = await cache.GetAsync("user:123", ct);
if (bytes != null)
{
    // Process raw bytes
}
```

---

#### SetAsync (Raw Bytes)

Set raw bytes for a key with optional TTL.

```csharp
ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CacheEntryOptions options, CancellationToken ct);
```

**Parameters**:
- `key`: Cache key
- `value`: Bytes to cache (uses `ReadOnlyMemory<byte>` for zero-copy)
- `options`: TTL and other options
- `ct`: Cancellation token

**Example**:
```csharp
var bytes = Encoding.UTF8.GetBytes("Hello, World!");
await cache.SetAsync(
    "greeting",
    bytes,
    new CacheEntryOptions(Ttl: TimeSpan.FromMinutes(5)),
    ct);
```

---

#### RemoveAsync

Remove a key from the cache.

```csharp
ValueTask<bool> RemoveAsync(string key, CancellationToken ct);
```

**Parameters**:
- `key`: Cache key
- `ct`: Cancellation token

**Returns**: `true` if key existed and was removed, `false` if key didn't exist

**Example**:
```csharp
var removed = await cache.RemoveAsync("user:123", ct);
if (removed)
{
    Console.WriteLine("User evicted from cache");
}
```

---

#### GetAsync<T> (Zero-Allocation)

Get and deserialize a typed value using custom deserializer.

```csharp
ValueTask<T?> GetAsync<T>(string key, SpanDeserializer<T> deserialize, CancellationToken ct);
```

**Parameters**:
- `key`: Cache key
- `deserialize`: Function that deserializes from `ReadOnlySpan<byte>`
- `ct`: Cancellation token

**Returns**: Deserialized value, or `null` if key doesn't exist

**Example (JSON)**:
```csharp
var user = await cache.GetAsync(
    "user:123",
    span => JsonSerializer.Deserialize<User>(span),
    ct);
```

**Example (MessagePack)**:
```csharp
var user = await cache.GetAsync(
    "user:123",
    span => MessagePackSerializer.Deserialize<User>(span),
    ct);
```

---

#### SetAsync<T> (Zero-Allocation)

Serialize and cache a typed value using custom serializer.

```csharp
ValueTask SetAsync<T>(string key, T value, Action<IBufferWriter<byte>, T> serialize, CacheEntryOptions options, CancellationToken ct);
```

**Parameters**:
- `key`: Cache key
- `value`: Value to cache
- `serialize`: Function that serializes to `IBufferWriter<byte>`
- `options`: TTL and other options
- `ct`: Cancellation token

**Example (JSON)**:
```csharp
await cache.SetAsync(
    "user:123",
    user,
    (writer, u) =>
    {
        using var jsonWriter = new Utf8JsonWriter(writer);
        JsonSerializer.Serialize(jsonWriter, u);
    },
    new CacheEntryOptions(Ttl: TimeSpan.FromMinutes(10)),
    ct);
```

**Example (MessagePack)**:
```csharp
await cache.SetAsync(
    "user:123",
    user,
    (writer, u) => MessagePackSerializer.Serialize(writer, u),
    new CacheEntryOptions(Ttl: TimeSpan.FromMinutes(10)),
    ct);
```

---

#### GetOrSetAsync<T> (Cache-Aside Pattern)

Get value from cache, or compute and cache it if missing. **Stampede-protected** with coalescing.

```csharp
ValueTask<T> GetOrSetAsync<T>(
    string key,
    Func<CancellationToken, ValueTask<T>> factory,
    Action<IBufferWriter<byte>, T> serialize,
    SpanDeserializer<T> deserialize,
    CacheEntryOptions options,
    CancellationToken ct);
```

**Parameters**:
- `key`: Cache key
- `factory`: Async function to compute value if cache miss
- `serialize`: Serialization function
- `deserialize`: Deserialization function
- `options`: TTL for cached value
- `ct`: Cancellation token

**Returns**: Cached or computed value (never null)

**Stampede Protection**: If 1000 threads request the same missing key simultaneously, only **one** will execute the factory. The other 999 will wait and receive the same result.

**Example**:
```csharp
var user = await cache.GetOrSetAsync(
    $"user:{userId}",
    async ct =>
    {
        // This runs only on cache miss
        // Protected from thundering herd
        return await database.GetUserAsync(userId, ct);
    },
    (writer, u) =>
    {
        using var jsonWriter = new Utf8JsonWriter(writer);
        JsonSerializer.Serialize(jsonWriter, u);
    },
    span => JsonSerializer.Deserialize<User>(span)!,
    new CacheEntryOptions(Ttl: TimeSpan.FromMinutes(10)),
    ct);
```

---

### CacheEntryOptions

Configuration for cache entries.

**Namespace**: `VapeCache.Abstractions.Caching`

```csharp
public readonly record struct CacheEntryOptions(TimeSpan? Ttl = null);
```

**Properties**:
- `Ttl`: Time-to-live for the cache entry. `null` = no expiration (permanent).

**Examples**:
```csharp
// 5 minute TTL
new CacheEntryOptions(Ttl: TimeSpan.FromMinutes(5))

// 1 hour TTL
new CacheEntryOptions(Ttl: TimeSpan.FromHours(1))

// No expiration (permanent)
new CacheEntryOptions(Ttl: null)
// Or simply:
new CacheEntryOptions()
```

---

## Typed Collections API

High-level API for Redis data structures with automatic JSON serialization.

### ICacheCollectionFactory

Factory for creating typed collection wrappers.

**Namespace**: `VapeCache.Abstractions.Collections`

```csharp
public interface ICacheCollectionFactory
{
    ICacheList<T> List<T>(string key);
    ICacheSet<T> Set<T>(string key);
    ICacheHash<T> Hash<T>(string key);
}
```

**Injection**:
```csharp
public class MyService
{
    private readonly ICacheCollectionFactory _collections;

    public MyService(ICacheCollectionFactory collections)
    {
        _collections = collections;
    }
}
```

**Usage**:
```csharp
var todoList = _collections.List<TodoItem>("todos:user:123");
var activeUsers = _collections.Set<string>("users:active");
var userProfile = _collections.Hash<string>("user:123:profile");
```

---

### ICacheList<T>

Typed Redis LIST operations. Lists are **ordered collections** supporting push/pop from both ends.

**Namespace**: `VapeCache.Abstractions.Collections`

**Use Cases**:
- Task queues
- Activity feeds
- Recent items
- Undo/redo stacks

#### Interface Definition

```csharp
public interface ICacheList<T>
{
    string Key { get; }

    ValueTask<long> PushFrontAsync(T item, CancellationToken ct = default);
    ValueTask<long> PushBackAsync(T item, CancellationToken ct = default);
    ValueTask<T?> PopFrontAsync(CancellationToken ct = default);
    ValueTask<T?> PopBackAsync(CancellationToken ct = default);
    ValueTask<T[]> RangeAsync(long start, long stop, CancellationToken ct = default);
    ValueTask<long> LengthAsync(CancellationToken ct = default);
}
```

#### Methods

##### PushFrontAsync

Add item to the **front** (left/head) of the list.

```csharp
ValueTask<long> PushFrontAsync(T item, CancellationToken ct = default);
```

**Returns**: New length of the list

**Example**:
```csharp
var todoList = collections.List<TodoItem>("todos:user:123");

await todoList.PushFrontAsync(new TodoItem
{
    Id = Guid.NewGuid(),
    Title = "Urgent task",
    Priority = Priority.High
});
```

---

##### PushBackAsync

Add item to the **back** (right/tail) of the list.

```csharp
ValueTask<long> PushBackAsync(T item, CancellationToken ct = default);
```

**Returns**: New length of the list

**Example**:
```csharp
await todoList.PushBackAsync(new TodoItem
{
    Id = Guid.NewGuid(),
    Title = "Low priority task",
    Priority = Priority.Low
});
```

---

##### PopFrontAsync

Remove and return item from the **front** of the list.

```csharp
ValueTask<T?> PopFrontAsync(CancellationToken ct = default);
```

**Returns**: The item, or `null` if list is empty

**Example (Queue Pattern - FIFO)**:
```csharp
var taskQueue = collections.List<WorkItem>("tasks:pending");

// Producer: Add to back
await taskQueue.PushBackAsync(workItem);

// Consumer: Remove from front
var nextTask = await taskQueue.PopFrontAsync();
if (nextTask != null)
{
    await ProcessTaskAsync(nextTask);
}
```

---

##### PopBackAsync

Remove and return item from the **back** of the list.

```csharp
ValueTask<T?> PopBackAsync(CancellationToken ct = default);
```

**Returns**: The item, or `null` if list is empty

**Example (Stack Pattern - LIFO)**:
```csharp
var undoStack = collections.List<EditorAction>("editor:undo");

// Push to front (most recent)
await undoStack.PushFrontAsync(action);

// Pop from front (most recent)
var lastAction = await undoStack.PopFrontAsync();
```

---

##### RangeAsync

Get a range of items **without removing** them.

```csharp
ValueTask<T[]> RangeAsync(long start, long stop, CancellationToken ct = default);
```

**Parameters**:
- `start`: Zero-based start index (0 = first item, -1 = last item, -2 = second-to-last)
- `stop`: Zero-based stop index (inclusive)

**Returns**: Array of items in the range

**Examples**:
```csharp
var activityFeed = collections.List<Activity>("user:123:feed");

// Get first 10 items
var recent = await activityFeed.RangeAsync(0, 9);

// Get last 5 items
var latest = await activityFeed.RangeAsync(-5, -1);

// Get all items
var all = await activityFeed.RangeAsync(0, -1);

// Get items 10-20
var page2 = await activityFeed.RangeAsync(10, 20);
```

---

##### LengthAsync

Get the number of items in the list.

```csharp
ValueTask<long> LengthAsync(CancellationToken ct = default);
```

**Returns**: Number of items

**Example**:
```csharp
var count = await todoList.LengthAsync();
Console.WriteLine($"You have {count} todos");
```

---

### ICacheSet<T>

Typed Redis SET operations. Sets are **unordered collections of unique items**.

**Namespace**: `VapeCache.Abstractions.Collections`

**Use Cases**:
- Unique tags
- User permissions
- Online users
- Deduplication

#### Interface Definition

```csharp
public interface ICacheSet<T>
{
    string Key { get; }

    ValueTask<long> AddAsync(T item, CancellationToken ct = default);
    ValueTask<long> RemoveAsync(T item, CancellationToken ct = default);
    ValueTask<bool> ContainsAsync(T item, CancellationToken ct = default);
    ValueTask<T[]> MembersAsync(CancellationToken ct = default);
    ValueTask<long> CountAsync(CancellationToken ct = default);
}
```

#### Methods

##### AddAsync

Add an item to the set (idempotent - adding same item twice has no effect).

```csharp
ValueTask<long> AddAsync(T item, CancellationToken ct = default);
```

**Returns**:
- `1` if item was added (new)
- `0` if item already existed

**Example**:
```csharp
var tags = collections.Set<string>("article:123:tags");

var added = await tags.AddAsync("csharp");
// added = 1 (new tag)

var addedAgain = await tags.AddAsync("csharp");
// addedAgain = 0 (duplicate)
```

---

##### RemoveAsync

Remove an item from the set.

```csharp
ValueTask<long> RemoveAsync(T item, CancellationToken ct = default);
```

**Returns**:
- `1` if item was removed
- `0` if item wasn't in the set

**Example**:
```csharp
var removed = await tags.RemoveAsync("csharp");
// removed = 1 (was in set)

var removedAgain = await tags.RemoveAsync("csharp");
// removedAgain = 0 (not in set)
```

---

##### ContainsAsync

Check if an item exists in the set. **O(1)** operation.

```csharp
ValueTask<bool> ContainsAsync(T item, CancellationToken ct = default);
```

**Returns**: `true` if item is in set, `false` otherwise

**Example**:
```csharp
var onlineUsers = collections.Set<Guid>("users:online");

if (await onlineUsers.ContainsAsync(userId))
{
    Console.WriteLine("User is online");
}
```

---

##### MembersAsync

Get all items in the set.

```csharp
ValueTask<T[]> MembersAsync(CancellationToken ct = default);
```

**Returns**: Array of all items (order is **not guaranteed**)

**Example**:
```csharp
var allTags = await tags.MembersAsync();
foreach (var tag in allTags)
{
    Console.WriteLine($"Tag: {tag}");
}
```

---

##### CountAsync

Get the number of items in the set (cardinality).

```csharp
ValueTask<long> CountAsync(CancellationToken ct = default);
```

**Returns**: Number of unique items

**Example**:
```csharp
var userCount = await onlineUsers.CountAsync();
Console.WriteLine($"{userCount} users online");
```

---

### ICacheHash<T>

Typed Redis HASH operations. Hashes are **field-value maps** stored in a single Redis key.

**Namespace**: `VapeCache.Abstractions.Collections`

**Use Cases**:
- User profiles (multiple fields per user)
- Configuration settings
- Feature flags
- Session data

**Think of it as**: A `Dictionary<string, T>` stored in Redis under a single key.

#### Interface Definition

```csharp
public interface ICacheHash<T>
{
    string Key { get; }

    ValueTask<long> SetAsync(string field, T value, CancellationToken ct = default);
    ValueTask<T?> GetAsync(string field, CancellationToken ct = default);
    ValueTask<T?[]> GetManyAsync(string[] fields, CancellationToken ct = default);
}
```

#### Methods

##### SetAsync

Set a field in the hash.

```csharp
ValueTask<long> SetAsync(string field, T value, CancellationToken ct = default);
```

**Parameters**:
- `field`: Field name
- `value`: Value to set
- `ct`: Cancellation token

**Returns**:
- `1` if field is new
- `0` if field already existed (value updated)

**Example**:
```csharp
var profile = collections.Hash<string>("user:123:profile");

await profile.SetAsync("name", "John Doe");
await profile.SetAsync("email", "john@example.com");
await profile.SetAsync("bio", "Software developer");
```

---

##### GetAsync

Get a single field from the hash.

```csharp
ValueTask<T?> GetAsync(string field, CancellationToken ct = default);
```

**Returns**: Field value, or `null` if field doesn't exist

**Example**:
```csharp
var email = await profile.GetAsync("email");
// email = "john@example.com"

var phone = await profile.GetAsync("phone");
// phone = null (field doesn't exist)
```

---

##### GetManyAsync

Get multiple fields in a single round-trip.

```csharp
ValueTask<T?[]> GetManyAsync(string[] fields, CancellationToken ct = default);
```

**Parameters**:
- `fields`: Array of field names to retrieve

**Returns**: Array of values (same order as `fields`). `null` for missing fields.

**Example**:
```csharp
var values = await profile.GetManyAsync(new[] { "name", "email", "phone" });
// values[0] = "John Doe"
// values[1] = "john@example.com"
// values[2] = null (phone doesn't exist)
```

---

## Serialization

VapeCache uses **System.Text.Json** by default for typed collections. You can customize serialization.

### Default JSON Serialization

Typed collections automatically use JSON:

```csharp
public record User(string Name, string Email);

var list = collections.List<User>("users");
await list.PushBackAsync(new User("Alice", "alice@example.com"));

var user = await list.PopFrontAsync();
// Automatically serialized/deserialized as JSON
```

### Custom Serialization (Core API)

For zero-allocation or custom formats, use `ICacheService` directly:

**MessagePack Example**:
```csharp
await cache.SetAsync(
    "user:123",
    user,
    (writer, u) => MessagePackSerializer.Serialize(writer, u),
    new CacheEntryOptions(Ttl: TimeSpan.FromMinutes(10)),
    ct);

var user = await cache.GetAsync(
    "user:123",
    span => MessagePackSerializer.Deserialize<User>(span),
    ct);
```

**Protobuf Example**:
```csharp
await cache.SetAsync(
    "user:123",
    user,
    (writer, u) => Serializer.Serialize(writer, u),
    new CacheEntryOptions(Ttl: TimeSpan.FromMinutes(10)),
    ct);

var user = await cache.GetAsync(
    "user:123",
    span =>
    {
        var reader = new ReadOnlySequence<byte>(span);
        return Serializer.Deserialize<User>(reader);
    },
    ct);
```

---

## Error Handling

VapeCache uses the **Result<T> pattern** internally. Methods don't throw exceptions for expected failures (connection timeouts, network errors).

### Circuit Breaker Automatic Failover

When Redis is unavailable, the circuit breaker automatically fails over to in-memory mode:

```csharp
// Works regardless of Redis state
var user = await cache.GetOrSetAsync(
    "user:123",
    async ct => await database.GetUserAsync(userId, ct),
    serialize,
    deserialize,
    options,
    ct);

// If Redis is down:
// 1. Circuit opens within ~1 second
// 2. Switches to in-memory cache
// 3. Your code continues working
// 4. Circuit tests Redis every 10s/20s/40s... (exponential backoff)
// 5. Auto-recovers when Redis comes back
```

See [CIRCUIT_BREAKER.md](CIRCUIT_BREAKER.md) for configuration.

### Exceptions You Might See

**OperationCanceledException**: Thrown if you cancel the `CancellationToken`

```csharp
var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
try
{
    var user = await cache.GetAsync("user:123", deserialize, cts.Token);
}
catch (OperationCanceledException)
{
    // Your timeout triggered
}
```

**JsonException**: Thrown if deserialization fails (corrupted data)

```csharp
try
{
    var user = await cache.GetAsync(
        "user:123",
        span => JsonSerializer.Deserialize<User>(span),
        ct);
}
catch (JsonException ex)
{
    // Cached data is not valid JSON
    logger.LogWarning(ex, "Failed to deserialize user");
    await cache.RemoveAsync("user:123", ct); // Evict corrupted data
}
```

---

## Performance Patterns

### Zero-Allocation Serialization

Use `IBufferWriter<byte>` and `ReadOnlySpan<byte>` to avoid allocations:

```csharp
await cache.SetAsync(
    "key",
    data,
    (writer, d) =>
    {
        using var jsonWriter = new Utf8JsonWriter(writer);
        JsonSerializer.Serialize(jsonWriter, d);
        // No intermediate byte[] allocation
    },
    options,
    ct);

var data = await cache.GetAsync(
    "key",
    span => JsonSerializer.Deserialize<MyData>(span),
    // Deserialize directly from span, no copy
    ct);
```

### Batch Operations

Use typed collections' batch methods:

```csharp
var hash = collections.Hash<string>("user:123:profile");

// Get multiple fields in one round-trip
var values = await hash.GetManyAsync(new[] { "name", "email", "phone" });

// More efficient than:
var name = await hash.GetAsync("name");
var email = await hash.GetAsync("email");
var phone = await hash.GetAsync("phone");
```

### Stampede Protection

Use `GetOrSetAsync` instead of manual check-and-set:

**❌ Bad (Thundering Herd)**:
```csharp
var user = await cache.GetAsync("user:123", deserialize, ct);
if (user == null)
{
    // If 1000 requests hit simultaneously, all 1000 query the database!
    user = await database.GetUserAsync(123, ct);
    await cache.SetAsync("user:123", user, serialize, options, ct);
}
```

**✅ Good (Coalesced)**:
```csharp
var user = await cache.GetOrSetAsync(
    "user:123",
    async ct => await database.GetUserAsync(123, ct),
    serialize,
    deserialize,
    options,
    ct);
// Only ONE request executes the factory, others wait and share the result
```

### Connection Pooling

VapeCache automatically pools connections. Inject singleton `ICacheService`:

```csharp
services.AddSingleton<ICacheService, HybridCacheService>();

public class MyController
{
    private readonly ICacheService _cache; // Singleton, reuses connections

    public MyController(ICacheService cache)
    {
        _cache = cache;
    }
}
```

---

## Examples

### Example 1: User Profile Cache

```csharp
public class UserService
{
    private readonly ICacheService _cache;
    private readonly IUserRepository _db;

    public async Task<User> GetUserAsync(Guid userId, CancellationToken ct)
    {
        return await _cache.GetOrSetAsync(
            $"user:{userId}",
            async ct => await _db.GetUserByIdAsync(userId, ct),
            (writer, user) =>
            {
                using var jsonWriter = new Utf8JsonWriter(writer);
                JsonSerializer.Serialize(jsonWriter, user);
            },
            span => JsonSerializer.Deserialize<User>(span)!,
            new CacheEntryOptions(Ttl: TimeSpan.FromMinutes(10)),
            ct);
    }

    public async Task UpdateUserAsync(User user, CancellationToken ct)
    {
        await _db.UpdateUserAsync(user, ct);

        // Invalidate cache
        await _cache.RemoveAsync($"user:{user.Id}", ct);
    }
}
```

---

### Example 2: Real-Time Activity Feed

```csharp
public class ActivityService
{
    private readonly ICacheCollectionFactory _collections;

    public async Task AddActivityAsync(Guid userId, Activity activity)
    {
        var feed = _collections.List<Activity>($"feed:{userId}");

        // Add to front (most recent first)
        await feed.PushFrontAsync(activity);

        // Keep only last 100 activities
        var length = await feed.LengthAsync();
        if (length > 100)
        {
            // Remove oldest items (from back)
            for (int i = 0; i < length - 100; i++)
            {
                await feed.PopBackAsync();
            }
        }
    }

    public async Task<Activity[]> GetRecentActivitiesAsync(Guid userId)
    {
        var feed = _collections.List<Activity>($"feed:{userId}");

        // Get first 20 activities
        return await feed.RangeAsync(0, 19);
    }
}
```

---

### Example 3: Online Users Tracking

```csharp
public class PresenceService
{
    private readonly ICacheCollectionFactory _collections;

    public async Task UserConnectedAsync(Guid userId)
    {
        var onlineUsers = _collections.Set<Guid>("users:online");
        await onlineUsers.AddAsync(userId);
    }

    public async Task UserDisconnectedAsync(Guid userId)
    {
        var onlineUsers = _collections.Set<Guid>("users:online");
        await onlineUsers.RemoveAsync(userId);
    }

    public async Task<bool> IsUserOnlineAsync(Guid userId)
    {
        var onlineUsers = _collections.Set<Guid>("users:online");
        return await onlineUsers.ContainsAsync(userId);
    }

    public async Task<int> GetOnlineCountAsync()
    {
        var onlineUsers = _collections.Set<Guid>("users:online");
        return (int)await onlineUsers.CountAsync();
    }
}
```

---

### Example 4: Feature Flags

```csharp
public class FeatureFlagService
{
    private readonly ICacheCollectionFactory _collections;

    public async Task<bool> IsFeatureEnabledAsync(string featureName, Guid userId)
    {
        var flags = _collections.Hash<bool>($"features:user:{userId}");

        var enabled = await flags.GetAsync(featureName);
        return enabled ?? false; // Default to false if not set
    }

    public async Task SetFeatureFlagAsync(Guid userId, string featureName, bool enabled)
    {
        var flags = _collections.Hash<bool>($"features:user:{userId}");
        await flags.SetAsync(featureName, enabled);
    }

    public async Task<Dictionary<string, bool>> GetAllFlagsAsync(Guid userId)
    {
        var flags = _collections.Hash<bool>($"features:user:{userId}");

        var featureNames = new[] { "dark_mode", "beta_features", "email_notifications" };
        var values = await flags.GetManyAsync(featureNames);

        return featureNames
            .Zip(values, (name, value) => new { name, value })
            .ToDictionary(x => x.name, x => x.value ?? false);
    }
}
```

---

### Example 5: Task Queue (Producer-Consumer)

```csharp
public class TaskQueueService
{
    private readonly ICacheCollectionFactory _collections;

    // Producer: Add tasks to queue
    public async Task EnqueueAsync(WorkItem task)
    {
        var queue = _collections.List<WorkItem>("tasks:pending");

        // Add to back of queue (FIFO)
        await queue.PushBackAsync(task);
    }

    // Consumer: Process tasks from queue
    public async Task<WorkItem?> DequeueAsync()
    {
        var queue = _collections.List<WorkItem>("tasks:pending");

        // Remove from front of queue (FIFO)
        return await queue.PopFrontAsync();
    }

    public async Task<int> GetQueueLengthAsync()
    {
        var queue = _collections.List<WorkItem>("tasks:pending");
        return (int)await queue.LengthAsync();
    }
}

// Background worker
public class TaskWorker : BackgroundService
{
    private readonly TaskQueueService _queue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var task = await _queue.DequeueAsync();
            if (task != null)
            {
                await ProcessTaskAsync(task);
            }
            else
            {
                // Queue empty, wait before checking again
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }
}
```

---

### Example 6: Shopping Cart

```csharp
public class ShoppingCartService
{
    private readonly ICacheCollectionFactory _collections;

    public async Task AddItemAsync(Guid userId, CartItem item)
    {
        var cart = _collections.Hash<CartItem>($"cart:{userId}");

        // Use product ID as field key
        await cart.SetAsync(item.ProductId.ToString(), item);
    }

    public async Task RemoveItemAsync(Guid userId, Guid productId)
    {
        var cart = _collections.Hash<CartItem>($"cart:{userId}");

        // Redis HDEL is not in ICacheHash yet, use raw cache
        // This is a limitation of the current typed API
        // For now, set to null or use RemoveAsync from ICacheService
    }

    public async Task<CartItem[]> GetCartItemsAsync(Guid userId, Guid[] productIds)
    {
        var cart = _collections.Hash<CartItem>($"cart:{userId}");

        var items = await cart.GetManyAsync(
            productIds.Select(id => id.ToString()).ToArray());

        return items.Where(x => x != null).ToArray()!;
    }
}
```

---

## Related Documentation

- [Circuit Breaker Configuration](CIRCUIT_BREAKER.md) - Automatic failover and retry policies
- [Configuration Guide](CONFIGURATION.md) - Connection pooling, timeouts, TLS
- [Performance Benchmarks](PERFORMANCE.md) - Benchmark results and comparisons
- [Architecture](architecture.md) - Internal design and patterns

---

## Support

For questions, issues, or feature requests:
- GitHub Issues: [https://github.com/your-repo/vapecache/issues](https://github.com/your-repo/vapecache/issues)
- Documentation: [https://vapecache.dev/docs](https://vapecache.dev/docs)
