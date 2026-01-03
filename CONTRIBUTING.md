# Contributing to VapeCache

Thanks for your interest in improving VapeCache. This guide keeps the repo consistent and buildable.

## Prerequisites
- .NET 10 SDK
- Redis (local or remote) for integration tests

## Project Conventions
- Autofac-first: register services in Autofac modules and wire them via `As<T>()` on a single instance.
- Use Microsoft.Extensions.DependencyInjection only when a library API requires it (Autofac’s service provider factory will consume those registrations).
- Avoid proxy/adapter services and avoid service-locator helpers.

## Build
```bash
dotnet build VapeCache.sln -c Release
```

## Test
```bash
dotnet test VapeCache.sln -c Release
```

Integration tests require Redis. Configure via:
```bash
$env:VAPECACHE_REDIS_CONNECTIONSTRING = "redis://localhost:6379/0"
```

## Documentation
- Update `docs/INDEX.md` when adding or renaming docs.
- Keep examples compile-ready (prefer `CacheEntryOptions(Ttl: ...)`).
- If you add new public APIs, update `docs/API_REFERENCE.md` and `docs/REDIS_PROTOCOL_SUPPORT.md`.

## Pull Requests
- Keep changes scoped and explain rationale.
- Add/adjust tests for behavior changes.
- Avoid large formatting-only diffs unless required.
