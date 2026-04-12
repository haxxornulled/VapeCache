# VapeCache.Extensions.KeyDB

Explicit KeyDB package boundary for VapeCache runtime registration.

This package reuses the proven Redis-protocol runtime and provides KeyDB-first DI entry points:

- AddVapeCacheKeyDb()
- AddVapeCacheKeyDb(IConfiguration)

By default, configuration binding reads connection settings from the KeyDbConnection section.

## Install

```bash
dotnet add package VapeCache.Extensions.KeyDB
```

## Usage

```csharp
using VapeCache.Extensions.KeyDB;

builder.Services.AddVapeCacheKeyDb(builder.Configuration)
    .WithCacheStampedeProfile(CacheStampedeProfile.Balanced);
```

## Configuration

```json
{
  "KeyDbConnection": {
    "Host": "localhost",
    "Port": 6379,
    "Database": 0
  }
}
```

If you prefer existing RedisConnection section names, override the binding options callback.
