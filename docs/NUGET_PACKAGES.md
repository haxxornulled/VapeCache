# NuGet Packages

VapeCache ships a small set of packages to keep dependencies clean and optional.

## Licensing

- Community license: non-commercial only (`LICENSE`)
- Commercial/production use: requires paid Enterprise licensing
- Enterprise packages (`VapeCache.Persistence`, `VapeCache.Reconciliation`) are distributed from this repo under `LICENSE-ENTERPRISE.txt`
- `VapeCache.Licensing` is now owned by the dedicated `VapeCache.Licensing` repository and published to GitHub Packages (`https://nuget.pkg.github.com/haxxornulled/index.json`)

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

### VapeCache.Features.Invalidation
Optional policy-driven invalidation engine (tags/zones/keys + profiles).

```bash
dotnet add package VapeCache.Features.Invalidation
```

### VapeCache.Reconciliation
Optional reconciliation service to sync in-memory writes after Redis recovery.

```bash
dotnet add package VapeCache.Reconciliation
```

## Notes
- First-party package install/restore is smoke-tested in both CI and release workflows against the built `.nupkg` artifacts.
- The enterprise repo includes [NuGet.config](../NuGet.config) so `VapeCache.Persistence` and `VapeCache.Reconciliation` restore `VapeCache.Licensing` from GitHub Packages.
- In GitHub Actions, set `VAPECACHE_LICENSING_PACKAGES_TOKEN` (and `VAPECACHE_LICENSING_PACKAGES_USER` if needed) to read the private licensing package; workflows fall back to `GITHUB_TOKEN` when package permissions are already granted.
- For local restores, authenticate the `GitHubPackages` source with a GitHub PAT that has `read:packages`.
- For direct DI registration, bind `RedisConnection` before calling `AddVapecacheRedisConnections()` / `AddVapecacheCaching()`, or set `VAPECACHE_REDIS_CONNECTIONSTRING`.
- `WithAutoMappedEndpoints(...)` only maps wrapper routes when `VapeCacheEndpointOptions.Enabled = true`.
- Use `tools/publish-release-packages.ps1` to push the built packages in dependency-safe order when publishing to a NuGet feed.
- Logging is via `ILogger<T>` only; you choose Serilog/NLog/console in your host project.
- OpenTelemetry exporters are configured by the host; VapeCache emits metrics/traces via standard `Meter`/`ActivitySource`.
- [UPGRADE_NOTES.md](UPGRADE_NOTES.md) tracks release-critical behavior changes.
