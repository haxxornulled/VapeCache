# NuGet Packages

VapeCache ships a small set of packages to keep dependencies clean and optional.

## Licensing

- Community license: non-commercial only (`LICENSE.md`)
- Commercial/production use: requires paid Enterprise licensing
- Enterprise packages (`VapeCache.Persistence`, `VapeCache.Reconciliation`, `VapeCache.Licensing`) are distributed under `LICENSE-ENTERPRISE.txt`

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
- First-party package install/restore is smoke-tested in both CI and release workflows against the built `.nupkg` artifacts.
- For direct DI registration, bind `RedisConnection` before calling `AddVapecacheRedisConnections()` / `AddVapecacheCaching()`, or set `VAPECACHE_REDIS_CONNECTIONSTRING`.
- `WithAutoMappedEndpoints(...)` only maps wrapper routes when `VapeCacheEndpointOptions.Enabled = true`.
- Use `tools/publish-release-packages.ps1` to push the built packages in dependency-safe order when publishing to a NuGet feed.
- Logging is via `ILogger<T>` only; you choose Serilog/NLog/console in your host project.
- OpenTelemetry exporters are configured by the host; VapeCache emits metrics/traces via standard `Meter`/`ActivitySource`.
- [UPGRADE_NOTES.md](UPGRADE_NOTES.md) tracks release-critical behavior changes.
