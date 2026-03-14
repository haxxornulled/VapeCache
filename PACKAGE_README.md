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
dotnet add package VapeCache.Extensions.Logging
dotnet add package VapeCache.Extensions.PubSub
dotnet add package VapeCache.Extensions.Streams
dotnet add package VapeCache.Extensions.EntityFrameworkCore
dotnet add package VapeCache.Extensions.EntityFrameworkCore.OpenTelemetry
dotnet add package VapeCache.Extensions.AspNetCore
dotnet add package VapeCache.Extensions.Aspire
dotnet add package VapeCache.Features.Invalidation
```

## Published Packages

| Package | NuGet | GitHub Packages |
|---|---|---|
| `VapeCache.Runtime` | [NuGet](https://www.nuget.org/packages/VapeCache.Runtime) | [GitHub Packages](https://github.com/users/haxxornulled/packages/nuget/package/vapecache.runtime) |
| `VapeCache.Core` | [NuGet](https://www.nuget.org/packages/VapeCache.Core) | [GitHub Packages](https://github.com/users/haxxornulled/packages/nuget/package/vapecache.core) |
| `VapeCache.Abstractions` | [NuGet](https://www.nuget.org/packages/VapeCache.Abstractions) | [GitHub Packages](https://github.com/users/haxxornulled/packages/nuget/package/vapecache.abstractions) |
| `VapeCache.Features.Invalidation` | [NuGet](https://www.nuget.org/packages/VapeCache.Features.Invalidation) | [GitHub Packages](https://github.com/users/haxxornulled/packages/nuget/package/vapecache.features.invalidation) |
| `VapeCache.Extensions.DependencyInjection` | [NuGet](https://www.nuget.org/packages/VapeCache.Extensions.DependencyInjection) | [GitHub Packages](https://github.com/users/haxxornulled/packages/nuget/package/vapecache.extensions.dependencyinjection) |
| `VapeCache.Extensions.Logging` | [NuGet](https://www.nuget.org/packages/VapeCache.Extensions.Logging) | [GitHub Packages](https://github.com/users/haxxornulled/packages/nuget/package/vapecache.extensions.logging) |
| `VapeCache.Extensions.PubSub` | [NuGet](https://www.nuget.org/packages/VapeCache.Extensions.PubSub) | [GitHub Packages](https://github.com/users/haxxornulled/packages/nuget/package/vapecache.extensions.pubsub) |
| `VapeCache.Extensions.Streams` | [NuGet](https://www.nuget.org/packages/VapeCache.Extensions.Streams) | [GitHub Packages](https://github.com/users/haxxornulled/packages/nuget/package/vapecache.extensions.streams) |
| `VapeCache.Extensions.EntityFrameworkCore` | [NuGet](https://www.nuget.org/packages/VapeCache.Extensions.EntityFrameworkCore) | [GitHub Packages](https://github.com/users/haxxornulled/packages/nuget/package/vapecache.extensions.entityframeworkcore) |
| `VapeCache.Extensions.EntityFrameworkCore.OpenTelemetry` | [NuGet](https://www.nuget.org/packages/VapeCache.Extensions.EntityFrameworkCore.OpenTelemetry) | [GitHub Packages](https://github.com/users/haxxornulled/packages/nuget/package/vapecache.extensions.entityframeworkcore.opentelemetry) |
| `VapeCache.Extensions.AspNetCore` | [NuGet](https://www.nuget.org/packages/VapeCache.Extensions.AspNetCore) | [GitHub Packages](https://github.com/users/haxxornulled/packages/nuget/package/vapecache.extensions.aspnetcore) |
| `VapeCache.Extensions.Aspire` | [NuGet](https://www.nuget.org/packages/VapeCache.Extensions.Aspire) | [GitHub Packages](https://github.com/users/haxxornulled/packages/nuget/package/vapecache.extensions.aspire) |

## OSS Packages

- `VapeCache.Runtime`: Redis transport, caching runtime, fallback behavior, telemetry
- `VapeCache.Core`: shared primitives used by other VapeCache packages
- `VapeCache.Abstractions`: public contracts, options, and shared value types
- `VapeCache.Extensions.DependencyInjection`: one-call DI facade for runtime registration
- `VapeCache.Extensions.Logging`: optional Serilog + OTEL logging package with file sink support and optional JSON formatting (`Serilog:Json:*`)
- `VapeCache.Extensions.PubSub`: optional Redis pub/sub registration package for publish/subscribe workloads
- `VapeCache.Extensions.Streams`: optional Redis 8.6 stream package for idempotent producer workflows (`XADD IDMP/IDMPAUTO`, `XCFGSET`)
- `VapeCache.Extensions.EntityFrameworkCore`: EF Core second-level cache interceptor contracts, deterministic query key builder, and save-changes invalidation bridge wiring
- `VapeCache.Extensions.EntityFrameworkCore.OpenTelemetry`: optional OTEL metrics/activity package for EF Core cache interceptor events
- `VapeCache.Extensions.AspNetCore`: ASP.NET Core output-cache integration
- `VapeCache.Extensions.Aspire`: Aspire integration, health checks, and telemetry wiring
- `VapeCache.Features.Invalidation`: optional invalidation policies for keys, tags, and zones

## OSS vs Enterprise Boundary

- Multiplexed transport is part of OSS.
- Adaptive autoscaling of multiplexed lanes is Enterprise.
- Enterprise capabilities focus on operational leverage (autoscaling, durable persistence, reconciliation, control-plane/admin/licensing), not basic OSS usability.

See: https://github.com/haxxornulled/VapeCache/blob/main/docs/OSS_VS_ENTERPRISE.md

## Documentation

- Quick start: https://github.com/haxxornulled/VapeCache/blob/main/docs/QUICKSTART.md
- Configuration: https://github.com/haxxornulled/VapeCache/blob/main/docs/CONFIGURATION.md
- Settings reference: https://github.com/haxxornulled/VapeCache/blob/main/docs/SETTINGS_REFERENCE.md
- ASP.NET Core integration: https://github.com/haxxornulled/VapeCache/blob/main/docs/ASPNETCORE_PIPELINE_CACHING.md
- Aspire integration: https://github.com/haxxornulled/VapeCache/blob/main/docs/ASPIRE_INTEGRATION.md
- License FAQ: https://github.com/haxxornulled/VapeCache/blob/main/docs/LICENSE_FAQ.md

## Source

https://github.com/haxxornulled/VapeCache

## License

VapeCache uses BUSL-1.1 with an Additional Use Grant.

- Allowed: production use, commercial application use, SaaS use, internal business use, modification, redistribution
- Restricted: offering VapeCache as a hosted caching/database service, or embedding it as the core of a commercial caching/database infrastructure product
- Change date: March 11, 2029 (converts to Apache 2.0)

See LICENSE in the package for full legal terms and the docs FAQ for quick guidance.
