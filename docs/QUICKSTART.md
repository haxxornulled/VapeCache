# VapeCache Quickstart Guide

Get VapeCache running in 5 minutes.

## Prerequisites

- .NET 10 SDK
- Redis server (localhost or remote)

## Step 1: Install (Coming Soon)

```bash
dotnet add package VapeCache.Infrastructure
```

For now, clone the repository:
```bash
git clone https://github.com/haxxornulled/VapeCache.git
cd VapeCache
```

## Step 2: Add VapeCache to Your Project

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);

// Add VapeCache services
builder.Services.AddVapecacheRedisConnections();
builder.Services.AddVapecacheCaching();

var app = builder.Build();
app.Run();
```

## Step 3: Configure Connection (appsettings.json)

```json
{
  "RedisConnection": {
    "Host": "localhost",
    "Port": 6379
  }
}
```

**Or via environment variable:**
```bash
export VAPECACHE_REDIS_CONNECTIONSTRING="redis://localhost:6379/0"
```

## Step 4: Use the Cache

```csharp
public class UserService
{
    private readonly ICacheService _cache;

    public UserService(ICacheService cache) => _cache = cache;

    public async Task<User?> GetUserAsync(int id)
    {
        var key = $"user:{id}";

        // Simple GET
        var bytes = await _cache.GetAsync(key, CancellationToken.None);
        if (bytes != null)
            return JsonSerializer.Deserialize<User>(bytes);

        // Fetch from DB
        var user = await _db.Users.FindAsync(id);

        // SET with 5-minute TTL
        var json = JsonSerializer.SerializeToUtf8Bytes(user);
        await _cache.SetAsync(
            key,
            json,
            new CacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
            },
            CancellationToken.None);

        return user;
    }
}
```

## Step 5: Use Get-or-Set Pattern (Recommended)

```csharp
public async Task<User?> GetUserAsync(int id, CancellationToken ct)
{
    var key = $"user:{id}";

    return await _cache.GetOrSetAsync(
        key,
        async ct => await _db.Users.FindAsync(id, ct), // Factory
        (writer, user) => JsonSerializer.Serialize(writer, user), // Serialize
        bytes => JsonSerializer.Deserialize<User>(bytes), // Deserialize
        new CacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) },
        ct);
}
```

**Benefits:**
- Single method call
- Stampede protection (coalesces concurrent requests)
- Circuit breaker (falls back to memory if Redis is down)

## Step 6: Verify It Works

Run the console host to test the connection:

```bash
dotnet run --project VapeCache.Console -c Release
```

HTTP endpoints available at `http://localhost:5080`:
- `GET /healthz` - Check if Redis is connected
- `GET /cache/stats` - View hit/miss rates
- `PUT /cache/test?ttlSeconds=60` - Store a value (body = text)
- `GET /cache/test` - Retrieve the value

## Next Steps

- [Configuration Guide](CONFIGURATION.md) - Full appsettings.json reference
- [Architecture Overview](ARCHITECTURE.md) - How VapeCache works internally
- [Observability](OBSERVABILITY_ARCHITECTURE.md) - Metrics, traces, and logs
- [.NET Aspire Integration](ASPIRE_INTEGRATION.md) - Cloud-native deployment
