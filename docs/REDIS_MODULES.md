# Redis Module Detection

**Status:** ✅ COMPLETE (Phase 3 - Detection Only)
**Date:** December 25, 2025

## Overview

VapeCache can detect which Redis modules are installed on your server, enabling conditional feature activation. Currently supports **module detection** via the `MODULE LIST` command - actual module-specific operations (like RedisJSON) will be added incrementally.

## Why Module Detection?

Redis modules extend Redis with powerful capabilities:
- **RedisJSON** - Native JSON document storage with JSONPath queries
- **RediSearch** - Full-text search and secondary indexing
- **RedisBloom** - Probabilistic data structures (Bloom filters, Cuckoo filters)
- **RedisGraph** - Graph database operations
- **RedisTimeSeries** - Time-series data management

VapeCache detects these modules at runtime and can enable enhanced features automatically.

## API Reference

### IRedisModuleDetector

```csharp
public interface IRedisModuleDetector
{
    /// <summary>
    /// Check if a specific module is installed.
    /// Common modules: "ReJSON", "bf", "search", "graph", "timeseries"
    /// </summary>
    ValueTask<bool> IsModuleInstalledAsync(string moduleName, CancellationToken ct = default);

    /// <summary>
    /// Get all installed module names.
    /// </summary>
    ValueTask<string[]> GetInstalledModulesAsync(CancellationToken ct = default);

    /// <summary>
    /// Check if RedisJSON module is available (enables native JSON operations).
    /// </summary>
    ValueTask<bool> HasRedisJsonAsync(CancellationToken ct = default);
}
```

**Registration:** Automatically available via DI when using `AddVapecacheCaching()`

## Usage Examples

### Example 1: Check for RedisJSON

```csharp
public class CacheService
{
    private readonly IRedisModuleDetector _moduleDetector;
    private readonly ILogger<CacheService> _logger;

    public CacheService(IRedisModuleDetector moduleDetector, ILogger<CacheService> logger)
    {
        _moduleDetector = moduleDetector;
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        if (await _moduleDetector.HasRedisJsonAsync())
        {
            _logger.LogInformation("RedisJSON detected - native JSON operations enabled");
            // Future: Enable JSON.GET, JSON.SET commands
        }
        else
        {
            _logger.LogInformation("RedisJSON not available - using serialized JSON");
        }
    }
}
```

### Example 2: List All Modules

```csharp
public async Task LogInstalledModulesAsync()
{
    var detector = serviceProvider.GetRequiredService<IRedisModuleDetector>();
    var modules = await detector.GetInstalledModulesAsync();

    if (modules.Length == 0)
    {
        _logger.LogInformation("No Redis modules installed (vanilla Redis)");
    }
    else
    {
        _logger.LogInformation("Installed Redis modules: {Modules}", string.Join(", ", modules));
    }
}
```

### Example 3: Feature Flags Based on Modules

```csharp
public class FeatureService
{
    private readonly IRedisModuleDetector _detector;

    public async Task<FeatureFlags> GetAvailableFeaturesAsync()
    {
        var modules = await _detector.GetInstalledModulesAsync();

        return new FeatureFlags
        {
            JsonDocuments = modules.Contains("ReJSON"),
            FullTextSearch = modules.Contains("search") || modules.Contains("ft"),
            BloomFilters = modules.Contains("bf"),
            GraphDatabase = modules.Contains("graph"),
            TimeSeries = modules.Contains("timeseries")
        };
    }
}
```

## Performance

### Caching Strategy

Module detection results are **cached in-memory** to avoid repeated network calls:

```csharp
private string[]? _cachedModules;
private bool _modulesCached;

public async ValueTask<string[]> GetInstalledModulesAsync(CancellationToken ct = default)
{
    // Return cached result if available
    if (_modulesCached && _cachedModules is not null)
        return _cachedModules;

    // Query Redis once, cache forever
    var modules = await _executor.ModuleListAsync(ct);
    _cachedModules = modules;
    _modulesCached = true;
    return modules;
}
```

**First call:** ~1-2ms (network round-trip)
**Subsequent calls:** <0.001ms (in-memory cache hit)

### Error Handling

If `MODULE LIST` fails (old Redis version, permissions issue):
- Returns empty array `[]`
- Logs error but doesn't throw
- Caches the empty result to avoid retry storms

## Common Module Names

| Module Name     | Detection String | Description |
|----------------|-----------------|-------------|
| RedisJSON      | `ReJSON`        | Native JSON document storage |
| RediSearch     | `search` or `ft` | Full-text search and indexing |
| RedisBloom     | `bf`            | Probabilistic data structures |
| RedisGraph     | `graph`         | Graph database |
| RedisTimeSeries| `timeseries`    | Time-series data management |
| RedisGears     | `rg`            | Serverless functions |

## RedisJSON Integration (Future)

When RedisJSON is detected, VapeCache will enable:

### Native JSON Commands
```csharp
// Future API (not yet implemented)
var jsonCache = serviceProvider.GetRequiredService<IJsonCacheService>();

// Store JSON document natively (no serialization!)
await jsonCache.SetAsync("user:123", new User { Name = "Alice", Age = 30 });

// Query with JSONPath
var name = await jsonCache.GetPathAsync<string>("user:123", "$.name");
// Returns "Alice" without deserializing entire object!

// Update specific field (atomic!)
await jsonCache.SetPathAsync("user:123", "$.age", 31);
```

### Benefits of RedisJSON
1. **Partial Updates** - Update single fields without GET/modify/SET
2. **JSONPath Queries** - Extract nested data without full deserialization
3. **Atomic Operations** - Increment counters, append arrays atomically
4. **Space Efficient** - Redis stores JSON as optimized binary format

### Fallback Strategy
```csharp
// VapeCache will automatically choose the best approach:
if (await detector.HasRedisJsonAsync())
{
    // Use JSON.SET with native Redis storage
    await executor.JsonSetAsync("key", user);
}
else
{
    // Fallback to serialized binary with SET
    var buffer = new ArrayBufferWriter<byte>();
    JsonSerializer.Serialize(buffer, user);
    await executor.SetAsync("key", buffer.WrittenMemory, ttl: null, ct);
}
```

## Installing Redis Modules

### Docker
```bash
# Official Redis Stack (includes all modules)
docker run -p 6379:6379 redis/redis-stack:latest

# Just RedisJSON
docker run -p 6379:6379 redislabs/rejson:latest
```

### Redis Cloud
Modules are available on Redis Enterprise Cloud:
1. Create database
2. Enable modules in configuration
3. RedisJSON, RediSearch, RedisBloom included

### Standalone Redis
```bash
# Install module
wget https://redismodules.s3.amazonaws.com/rejson/rejson.Linux-x86_64.latest.so

# Load in redis.conf
loadmodule /path/to/rejson.so

# Or load at runtime
redis-cli MODULE LOAD /path/to/rejson.so
```

## Testing Module Detection

### Unit Test
```csharp
[Fact]
public async Task DetectsRedisJson()
{
    var mockExecutor = new Mock<IRedisCommandExecutor>();
    mockExecutor.Setup(x => x.ModuleListAsync(default))
        .ReturnsAsync(new[] { "ReJSON", "search" });

    var detector = new RedisModuleDetector(mockExecutor.Object);

    Assert.True(await detector.HasRedisJsonAsync());
    Assert.True(await detector.IsModuleInstalledAsync("search"));
    Assert.False(await detector.IsModuleInstalledAsync("bf"));
}
```

### Integration Test
```csharp
[Fact]
public async Task DetectRealRedisModules()
{
    var services = new ServiceCollection();
    services.AddVapecacheCaching();
    services.Configure<RedisConnectionOptions>(o =>
        o.Endpoints = "localhost:6379"); // Redis Stack container

    var provider = services.BuildServiceProvider();
    var detector = provider.GetRequiredService<IRedisModuleDetector>();

    var modules = await detector.GetInstalledModulesAsync();
    _output.WriteLine($"Detected modules: {string.Join(", ", modules)}");

    // Assert RedisJSON is available in Redis Stack
    Assert.True(await detector.HasRedisJsonAsync());
}
```

## Implementation Details

### MODULE LIST Response Format

Redis returns modules as an array of arrays:
```
*2                              # 2 modules
*6                              # Module 1 metadata (6 fields)
$4                              # Field name length
name                            # Field name
$6                              # Value length
ReJSON                          # Module name
$3
ver
:20000                          # Version 2.0.0
*6                              # Module 2 metadata
$4
name
$2
ft                              # RediSearch module
$3
ver
:20800
```

VapeCache extracts just the module names:
```csharp
for (var i = 0; i < items.Length; i++)
{
    var moduleInfo = items[i].ArrayItems;
    if (moduleInfo.Length > 1 && moduleInfo[1].Kind == RespKind.BulkString)
    {
        result[i] = Encoding.UTF8.GetString(moduleInfo[1].Bulk ?? []);
    }
}
return result; // ["ReJSON", "ft"]
```

## Roadmap

### Phase 3 (Detection): ✅ COMPLETE
- [x] IRedisModuleDetector interface
- [x] MODULE LIST command implementation
- [x] HasRedisJsonAsync() helper
- [x] In-memory caching of results
- [x] Error handling for old Redis versions

### Phase 4 (RedisJSON): Future
- [ ] JSON.SET, JSON.GET, JSON.DEL commands
- [ ] JSONPath query support (JSON.GET with path)
- [ ] Partial updates (JSON.SET with path)
- [ ] Atomic operations (JSON.NUMINCRBY, JSON.ARRAPPEND)
- [ ] Automatic fallback to serialized JSON when module unavailable
- [ ] Typed API: `IJsonDocument<T>` with JSONPath methods

### Phase 5 (Other Modules): Future
- [ ] RediSearch integration (full-text search)
- [ ] RedisBloom integration (probabilistic filters)
- [ ] RedisTimeSeries integration

## See Also

- [TYPED_COLLECTIONS.md](TYPED_COLLECTIONS.md) - Typed LIST/SET/HASH APIs
- [RICH_API_DESIGN.md](RICH_API_DESIGN.md) - Overall API design
- [RedisJSON Documentation](https://redis.io/docs/stack/json/)
- [Redis Modules Hub](https://redis.io/modules)

---

**Next Steps:** Once RedisJSON commands are implemented, VapeCache will automatically use them when the module is detected!
