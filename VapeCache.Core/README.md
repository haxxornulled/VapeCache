# VapeCache.Core

Shared primitives package used by other VapeCache packages.

Most applications should not install this package directly. It is usually brought in transitively by `VapeCache.Runtime`, `VapeCache.Abstractions`, or one of the extensions packages.

## Install

```bash
dotnet add package VapeCache.Core
```

## Use This Package When

- you are building against low-level shared primitives only
- you intentionally want the smallest shared dependency surface
- you are authoring supporting libraries around VapeCache packages

## Docs

- Package matrix: https://github.com/haxxornulled/VapeCache/blob/main/docs/NUGET_PACKAGES.md
- API reference: https://github.com/haxxornulled/VapeCache/blob/main/docs/API_REFERENCE.md
- Source repository: https://github.com/haxxornulled/VapeCache
