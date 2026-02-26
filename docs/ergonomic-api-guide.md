# VapeCache Ergonomic API Guide

The ergonomic API provides a high-level, type-safe interface over VapeCache's low-level `ICacheService` infrastructure. It eliminates the need to manually provide serialization delegates at every call site while maintaining zero-allocation performance on hot paths.

## Quick Start

### 1. DI Registration

```csharp
services.AddVapecacheCaching();
// That's it! IVapeCache is now registered with System.Text.Json codec by default
```

### 2. Basic Usage

```csharp
public class UserService
{
    private readonly IVapeCache _cache;
    private readonly IUserRepository _repository;

    public UserService(IVapeCache cache, IUserRepository repository)
    {
        _cache = cache;
        _repository = repository;
    }

    public async Task<UserDto> GetUserAsync(Guid userId, CancellationToken ct)
    {
        var key = CacheKey<UserDto>.From($"users:{userId}");

        return await _cache.GetOrCreateAsync(
            key,
            ct => _repository.GetUserAsync(userId, ct),
            options: new CacheEntryOptions(Ttl: TimeSpan.FromMinutes(10)),
            ct);
    }
}
```

## Cache Regions

Regions provide automatic key prefixing for organizing related cache entries:

```csharp
var users = _cache.Region("users");
var sessions = _cache.Region("sessions");

// Automatic prefixing: "users:{userId}"
var user = await users.GetOrCreateAsync(
    id: userId.ToString(),
    factory: ct => _repository.GetUserAsync(userId, ct),
    options: new CacheEntryOptions(TimeSpan.FromMinutes(10)),
    ct);

// Automatic prefixing: "sessions:{sessionId}"
await sessions.SetAsync(
    id: sessionId,
    value: sessionData,
    options: new CacheEntryOptions(TimeSpan.FromMinutes(30)));
```

## Custom Serialization

### Per-Type Custom Codecs

For performance-critical types, register custom binary codecs:

```csharp
// Example: Custom binary codec for high-frequency DTOs
public class UserDtoCodec : ICacheCodec<UserDto>
{
    public void Serialize(IBufferWriter<byte> buffer, UserDto value)
    {
        var span = buffer.GetSpan(256);
        var written = 0;

        // Write UserId (16 bytes)
        value.UserId.TryWriteBytes(span.Slice(written));
        written += 16;

        // Write Name length + UTF-8 bytes
        var nameBytes = Encoding.UTF8.GetBytes(value.Name);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(written), nameBytes.Length);
        written += 4;
        nameBytes.CopyTo(span.Slice(written));
        written += nameBytes.Length;

        buffer.Advance(written);
    }

    public UserDto Deserialize(ReadOnlySpan<byte> data)
    {
        var userId = new Guid(data.Slice(0, 16));
        var nameLen = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(16));
        var name = Encoding.UTF8.GetString(data.Slice(20, nameLen));

        return new UserDto { UserId = userId, Name = name };
    }
}

// Register at startup
services.AddVapecacheCaching();
services.AddSingleton<ICacheCodecProvider>(sp =>
{
    var provider = new SystemTextJsonCodecProvider();
    provider.Register(new UserDtoCodec());
    return provider;
});
```

### MessagePack Codec Provider

For maximum performance across all types:

```csharp
using MessagePack;

public class MessagePackCodecProvider : ICacheCodecProvider
{
    private readonly MessagePackSerializerOptions _options;
    private readonly ConcurrentDictionary<Type, object> _cache = new();

    public MessagePackCodecProvider()
    {
        _options = MessagePackSerializerOptions.Standard
            .WithCompression(MessagePackCompression.Lz4BlockArray);
    }

    public ICacheCodec<T> Get<T>()
    {
        if (_cache.TryGetValue(typeof(T), out var cached))
            return (ICacheCodec<T>)cached;

        var codec = new MessagePackCodec<T>(_options);
        _cache.TryAdd(typeof(T), codec);
        return codec;
    }

    private class MessagePackCodec<T> : ICacheCodec<T>
    {
        private readonly MessagePackSerializerOptions _options;

        public MessagePackCodec(MessagePackSerializerOptions options) => _options = options;

        public void Serialize(IBufferWriter<byte> buffer, T value)
            => MessagePackSerializer.Serialize(buffer, value, _options);

        public T Deserialize(ReadOnlySpan<byte> data)
            => MessagePackSerializer.Deserialize<T>(data, _options);
    }
}

// Register
services.AddSingleton<ICacheCodecProvider, MessagePackCodecProvider>();
```

## API Surface

### IVapeCache Methods

```csharp
public interface IVapeCache
{
    // Create logical regions for key organization
    ICacheRegion Region(string name);

    // Direct key-based access
    ValueTask<T?> GetAsync<T>(CacheKey<T> key, CancellationToken ct = default);
    ValueTask SetAsync<T>(CacheKey<T> key, T value, CacheEntryOptions options = default, CancellationToken ct = default);

    // Primary pattern: get-or-create with factory
    ValueTask<T> GetOrCreateAsync<T>(
        CacheKey<T> key,
        Func<CancellationToken, ValueTask<T>> factory,
        CacheEntryOptions options = default,
        CancellationToken ct = default);

    // Removal
    ValueTask<bool> RemoveAsync(CacheKey key, CancellationToken ct = default);
}
```

### ICacheRegion Methods

```csharp
public interface ICacheRegion
{
    string Name { get; }

    // Typed key generation with automatic prefixing
    CacheKey<T> Key<T>(string id);

    // Region-scoped operations (auto-prefixed)
    ValueTask<T?> GetAsync<T>(string id, CancellationToken ct = default);
    ValueTask SetAsync<T>(string id, T value, CacheEntryOptions options = default, CancellationToken ct = default);
    ValueTask<T> GetOrCreateAsync<T>(string id, Func<CancellationToken, ValueTask<T>> factory, CacheEntryOptions options = default, CancellationToken ct = default);
    ValueTask<bool> RemoveAsync(string id, CancellationToken ct = default);
}
```

## Advanced Patterns

### Stampede Protection

```csharp
// Enable stampede protection globally with fluent, profile-first options
services.AddOptions<CacheStampedeOptions>()
    .ConfigureCacheStampede(options =>
    {
        options.UseProfile(CacheStampedeProfile.Balanced)
            .WithMaxKeys(50_000)
            .WithLockWaitTimeout(TimeSpan.FromMilliseconds(750))
            .WithFailureBackoff(TimeSpan.FromMilliseconds(500));
    });

// Multiple concurrent requests will be coalesced to a single DB query
var user = await _cache.GetOrCreateAsync(key, LoadFromDb, new CacheEntryOptions(TimeSpan.FromMinutes(10)), ct);
```

### Circuit Breaker Integration

The ergonomic API automatically benefits from the hybrid cache's circuit breaker:

```csharp
// If Redis is down, automatically falls back to in-memory cache
var data = await _cache.GetOrCreateAsync(key, factory, options, ct);
```

### Conditional Updates

```csharp
var region = _cache.Region("counters");

// Only update if not exists
var existing = await region.GetAsync<int>("visitor-count", ct);
if (existing == null)
{
    await region.SetAsync("visitor-count", 1, ct: ct);
}
```

## Performance Characteristics

| Operation | Allocations | Notes |
|-----------|-------------|-------|
| GetOrCreateAsync (cache hit) | ~0-2 allocations | Span-based deserialization, codec cached |
| GetOrCreateAsync (cache miss) | ~2-4 allocations | Factory result + serialization buffer |
| Region creation | 1 allocation | Region instances are cheap |
| Codec retrieval | 0 allocations | Codecs are cached per type |

## Migration from ICacheService

**Before:**
```csharp
await _cache.SetAsync(
    "users:123",
    user,
    (buffer, u) => JsonSerializer.Serialize(buffer, u),
    new CacheEntryOptions(TimeSpan.FromMinutes(10)),
    ct);

var result = await _cache.GetAsync(
    "users:123",
    span => JsonSerializer.Deserialize<UserDto>(span),
    ct);
```

**After:**
```csharp
var key = CacheKey<UserDto>.From("users:123");

await _cache.SetAsync(key, user, new CacheEntryOptions(TimeSpan.FromMinutes(10)), ct);
var result = await _cache.GetAsync(key, ct);
```

## Best Practices

1. **Use Regions for Organization:** Group related keys to avoid collisions and simplify maintenance
2. **Cache Codec Instances:** Don't create codec providers per request - register once in DI
3. **Measure Before Optimizing:** Use System.Text.Json by default, switch to MessagePack only if profiling shows serialization overhead
4. **Leverage Typed Keys:** The type safety prevents runtime deserialization errors
5. **Enable Stampede Protection:** For expensive queries, always enable stampede protection to prevent cache stampedes

## See Also

- [Architecture Documentation](architecture.md)
- [Performance Gates](perf-gates.md)
- [Beating StackExchange.Redis Roadmap](../BEAT_STACKEXCHANGE_ROADMAP.md)
