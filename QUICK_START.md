# VapeCache Quick Start

This is the fastest path from zero to a working endpoint with VapeCache.

## Prerequisites

- .NET 10 SDK
- Redis 7+
- ASP.NET Core app

## Install

```bash
dotnet add package VapeCache.Runtime
```

Optional integrations:

```bash
dotnet add package VapeCache.Extensions.Aspire
dotnet add package VapeCache.Extensions.AspNetCore
```

## Run Redis

```bash
docker run --name vapecache-redis -p 6379:6379 -d redis:7
```

## Configure

`appsettings.json`:

```json
{
  "RedisConnection": {
    "Host": "localhost",
    "Port": 6379,
    "Database": 0
  },
  "RedisMultiplexer": {
    "EnableCommandInstrumentation": false
  }
}
```

Equivalent environment override:

```bash
setx VAPECACHE_REDIS_CONNECTIONSTRING "redis://localhost:6379/0"
```

Production TLS + ACL auth example:

```bash
setx VAPECACHE_REDIS_CONNECTIONSTRING "rediss://vapecache-app:pa%24%24w0rd%21@redis-prod.internal:6380/0?sni=redis-prod.internal"
```

Use URL-encoded credentials in connection strings when values contain reserved URI characters.

## Register Services

```csharp
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Connections;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<RedisConnectionOptions>()
    .Bind(builder.Configuration.GetSection("RedisConnection"));

builder.Services.AddVapecacheRedisConnections();
builder.Services.AddVapecacheCaching();

var app = builder.Build();
app.MapHealthChecks("/health");
app.Run();
```

## Validate

```bash
curl http://localhost:5000/health
```

## Full Docs

- Runtime guide: `README.md`
- Full configuration: `docs/CONFIGURATION.md`
- TLS + auth hardening: `docs/TLS_SECURITY.md`
- Complete settings catalog: `docs/SETTINGS_REFERENCE.md`
- API reference: `docs/API_REFERENCE.md`
- Invalidation docs: `docs/CACHE_INVALIDATION.md`
