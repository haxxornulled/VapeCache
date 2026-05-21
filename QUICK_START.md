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
dotnet add package VapeCache.Extensions.Logging
dotnet add package VapeCache.Extensions.PubSub
```

## Run Redis

```bash
docker run --name vapecache-redis -p 6379:6379 -d redis:8.6
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

Local development secret store (`AppHost` and direct `VapeCache.UI` runs share the same user-secrets ID):

```bash
dotnet user-secrets --project .\VapeCache.AppHost set "ConnectionStrings:redis" "redis://localhost:6379/0"
dotnet user-secrets --project .\VapeCache.AppHost set "Authentication:JwtBearer:SigningKey" "your-32+-byte-secret"
dotnet user-secrets --project .\VapeCache.AppHost set "Serilog:Seq:ApiKey" "your-seq-key"
```

Repeatable helper for key-by-key or appsettings-shaped updates:

```powershell
pwsh -File .\tools\manage-user-secrets.ps1 SetKey -Key "Serilog:Seq:ApiKey" -Value "your-seq-key"
pwsh -File .\tools\manage-user-secrets.ps1 SetBlock -Section "Authentication:JwtBearer" -JsonBlock '{ "SigningKey": "your-32+-byte-secret", "ValidIssuer": "vapecache-internal", "ValidAudience": "vapecache-admin" }'
pwsh -File .\tools\manage-user-secrets.ps1 RemoveSection -Section "Authentication:JwtBearer"
```

Equivalent environment variable names are:

- `VAPECACHE_REDIS_CONNECTIONSTRING` or `ConnectionStrings__redis`
- `Authentication__JwtBearer__SigningKey`
- `Serilog__Seq__ApiKey`

Optional JSON logs for shipping/parsing pipelines:

```json
{
  "Serilog": {
    "File": {
      "Enabled": true,
      "Path": "logs/vapecache-.log"
    },
    "Json": {
      "Enabled": true,
      "FileEnabled": true,
      "Formatter": "Compact"
    }
  }
}
```

## Register Services

```csharp
using VapeCache.Abstractions.Connections;
using VapeCache.Extensions.PubSub;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Connections;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<RedisConnectionOptions>()
    .Bind(builder.Configuration.GetSection("RedisConnection"));

builder.Services.AddVapeCacheRedisConnections();
builder.Services.AddVapeCacheCaching();
builder.Services.AddVapeCachePubSub(); // optional: only when pub/sub support is needed

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
- Complete settings catalog: `docs/SETTINGS_REFERENCE.md`
- API reference: `docs/API_REFERENCE.md`
- Invalidation docs: `docs/CACHE_INVALIDATION.md`
