# NuGet Packages

VapeCache ships a small set of packages to keep dependencies clean and optional.

## Packages

### VapeCache
Core implementation (Redis transport + hybrid cache + telemetry).

```bash
dotnet add package VapeCache
```

### VapeCache.Abstractions
Interfaces and value types only (for library authors).

```bash
dotnet add package VapeCache.Abstractions
```

### VapeCache.Extensions.Aspire
Aspire integration helpers.

```bash
dotnet add package VapeCache.Extensions.Aspire
```

### VapeCache.Extensions.AspNetCore
ASP.NET Core output-cache pipeline hooks (MVC/Blazor/Minimal API) backed by VapeCache storage.

```bash
dotnet add package VapeCache.Extensions.AspNetCore
```

### VapeCache.Reconciliation
Optional reconciliation service to sync in-memory writes after Redis recovery.

```bash
dotnet add package VapeCache.Reconciliation
```

## Notes
- Logging is via `ILogger<T>` only; you choose Serilog/NLog/console in your host project.
- OpenTelemetry exporters are configured by the host; VapeCache emits metrics/traces via standard `Meter`/`ActivitySource`.
