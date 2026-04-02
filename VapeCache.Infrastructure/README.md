# VapeCache.Runtime

Core Redis-first runtime package for VapeCache.

This package ships the transport, cache runtime, fallback behavior, circuit breaker, and telemetry hooks.

## Install

```bash
dotnet add package VapeCache.Runtime
```

## Use This Package When

- you want the core runtime without the DI facade package
- you are wiring services manually
- you need the Redis-first hybrid runtime surface

## Basic Registration

```csharp
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Connections;

builder.Services.AddOptions<RedisConnectionOptions>()
    .Bind(builder.Configuration.GetSection("RedisConnection"));

builder.Services.AddVapecacheRedisConnections();
builder.Services.AddVapecacheCaching();
```

If you want one-call registration with configuration binding, use `VapeCache.Extensions.DependencyInjection` instead.

## Docs

- Quick start: https://github.com/haxxornulled/VapeCache/blob/main/docs/QUICKSTART.md
- Configuration: https://github.com/haxxornulled/VapeCache/blob/main/docs/CONFIGURATION.md
- API reference: https://github.com/haxxornulled/VapeCache/blob/main/docs/API_REFERENCE.md
- Package matrix: https://github.com/haxxornulled/VapeCache/blob/main/docs/NUGET_PACKAGES.md
