# NuGet Packages

This OSS repository ships production-ready runtime packages for Redis-first caching on .NET.

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

- enterprise licensing/control-plane packages
- durable spill persistence package
- reconciliation package

## Release Notes

- Packages are built and smoke-tested from this repository.
- Runtime defaults and option coverage are documented in [SETTINGS_REFERENCE.md](SETTINGS_REFERENCE.md).
- Integration and migration guidance is tracked in [UPGRADE_NOTES.md](UPGRADE_NOTES.md).
