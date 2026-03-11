# VapeCache

VapeCache is a Redis-first caching runtime for ASP.NET Core and .NET services.

## Install

Primary runtime package:

```bash
dotnet add package VapeCache.Runtime
```

Optional integrations:

```bash
dotnet add package VapeCache.Extensions.DependencyInjection
dotnet add package VapeCache.Extensions.AspNetCore
dotnet add package VapeCache.Extensions.Aspire
dotnet add package VapeCache.Features.Invalidation
```

## OSS Packages

- `VapeCache.Runtime`: Redis transport, caching runtime, fallback behavior, telemetry
- `VapeCache.Core`: shared primitives used by other VapeCache packages
- `VapeCache.Abstractions`: public contracts, options, and shared value types
- `VapeCache.Extensions.DependencyInjection`: one-call DI facade for runtime registration
- `VapeCache.Extensions.AspNetCore`: ASP.NET Core output-cache integration
- `VapeCache.Extensions.Aspire`: Aspire integration, health checks, and telemetry wiring
- `VapeCache.Features.Invalidation`: optional invalidation policies for keys, tags, and zones

## Documentation

- Quick start: https://github.com/haxxornulled/VapeCache/blob/main/docs/QUICKSTART.md
- Configuration: https://github.com/haxxornulled/VapeCache/blob/main/docs/CONFIGURATION.md
- Settings reference: https://github.com/haxxornulled/VapeCache/blob/main/docs/SETTINGS_REFERENCE.md
- ASP.NET Core integration: https://github.com/haxxornulled/VapeCache/blob/main/docs/ASPNETCORE_PIPELINE_CACHING.md
- Aspire integration: https://github.com/haxxornulled/VapeCache/blob/main/docs/ASPIRE_INTEGRATION.md

## Source

https://github.com/haxxornulled/VapeCache
