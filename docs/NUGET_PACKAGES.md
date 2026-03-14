# NuGet Packages

This OSS repository ships production-ready runtime packages for Redis-first caching on .NET.

## Published Registries (NuGet + GitHub Packages)

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

## Install Matrix

### VapeCache.Runtime
Core runtime package (transport, cache API, fallback, telemetry).

```bash
dotnet add package VapeCache.Runtime
```

### VapeCache.Core
Shared primitives used by multiple VapeCache packages. Normally resolved transitively.

```bash
dotnet add package VapeCache.Core
```

### VapeCache.Abstractions
Contracts, options, and shared value types.

```bash
dotnet add package VapeCache.Abstractions
```

### VapeCache.Features.Invalidation
Optional invalidation policies (keys, tags, zones).

```bash
dotnet add package VapeCache.Features.Invalidation
```

### VapeCache.Extensions.DependencyInjection
DI facade package for clean architecture wiring. Registers runtime services and provides fluent configuration binding helpers.

```bash
dotnet add package VapeCache.Extensions.DependencyInjection
```

### VapeCache.Extensions.Logging
Optional logging package for Serilog + OpenTelemetry host wiring. Includes production-safe defaults, optional rolling file sink support, and optional JSON formatter routing (`Serilog:Json:*`).

```bash
dotnet add package VapeCache.Extensions.Logging
```

### VapeCache.Extensions.PubSub
Optional Redis pub/sub package. Adds explicit pub/sub registration for `IRedisPubSubService` with bounded delivery queues and reconnect/resubscribe behavior.

```bash
dotnet add package VapeCache.Extensions.PubSub
```

### VapeCache.Extensions.Streams
Optional Redis Streams package for idempotent producer flows on Redis 8.6 (`XADD IDMP/IDMPAUTO` and `XCFGSET` idempotence retention config).

```bash
dotnet add package VapeCache.Extensions.Streams
```

### VapeCache.Extensions.EntityFrameworkCore
Optional EF Core adapter package for second-level cache interceptor contracts, deterministic query-key generation, and save-changes invalidation bridge wiring.

```bash
dotnet add package VapeCache.Extensions.EntityFrameworkCore
```

### VapeCache.Extensions.EntityFrameworkCore.OpenTelemetry
Optional OpenTelemetry package for EF Core cache interceptor events. Emits OTEL metrics/activity signals and auto-enables observer callbacks for telemetry flows.

```bash
dotnet add package VapeCache.Extensions.EntityFrameworkCore.OpenTelemetry
```

### VapeCache.Extensions.AspNetCore
ASP.NET Core output-cache integration with VapeCache storage.

```bash
dotnet add package VapeCache.Extensions.AspNetCore
```

### VapeCache.Extensions.Aspire
Aspire integration helpers (health checks, telemetry wiring, endpoints).

```bash
dotnet add package VapeCache.Extensions.Aspire
```

## Package Boundaries

OSS includes only the packages above.

Not shipped from this OSS repo:

- adaptive autoscaling of multiplexed lanes
- enterprise licensing/control-plane packages
- durable spill persistence package
- reconciliation package for post-outage write replay

Clarification:
- multiplexing itself is OSS
- adaptive autoscaling is Enterprise

See [OSS_VS_ENTERPRISE.md](OSS_VS_ENTERPRISE.md) for the canonical boundary.

## License Summary

VapeCache is licensed under BUSL-1.1 with an Additional Use Grant.

Allowed:
- production use
- commercial application use
- SaaS use
- internal business use

Not allowed:
- offering VapeCache itself as a hosted caching/database service
- embedding VapeCache as the core of a commercial caching/database infrastructure product

Current versions convert to Apache 2.0 on 2029-03-11.

See [LICENSE_FAQ.md](LICENSE_FAQ.md) and [../LICENSE](../LICENSE).

## Release Notes

- Packages are built and smoke-tested from this repository.
- Runtime defaults and option coverage are documented in [SETTINGS_REFERENCE.md](SETTINGS_REFERENCE.md).
- Integration and migration guidance is tracked in [UPGRADE_NOTES.md](UPGRADE_NOTES.md).
