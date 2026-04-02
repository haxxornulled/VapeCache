# VapeCache.Abstractions

Public contracts, interfaces, and configuration types for VapeCache.

Use this package when you want to reference the API surface without bringing in the runtime implementation.

## Install

```bash
dotnet add package VapeCache.Abstractions
```

## Common Use Cases

- shared contracts between application and infrastructure layers
- referencing `IVapeCache`, `ICacheService`, `CacheEntryOptions`, and related types
- binding and validating VapeCache option objects in a host

Most applications that actually run VapeCache should also install `VapeCache.Runtime` or `VapeCache.Extensions.DependencyInjection`.

## Docs

- API reference: https://github.com/haxxornulled/VapeCache/blob/main/docs/API_REFERENCE.md
- Configuration: https://github.com/haxxornulled/VapeCache/blob/main/docs/CONFIGURATION.md
- Package matrix: https://github.com/haxxornulled/VapeCache/blob/main/docs/NUGET_PACKAGES.md
