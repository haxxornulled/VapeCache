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
dotnet build VapeCache.slnx -c Release
```

## Test
```bash
dotnet test VapeCache.slnx -c Release
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

## Commit Notifications
- Pushes to `main`, `master`, and release tags matching `v*` trigger `.github/workflows/commit-notify.yml`.
- Branch pushes are posted as comments on the repo's `Commit Notifications` issue, and release tag pushes are posted on `Release Notifications`.
- The workflow `@mention`s subscribed contributors on each notification comment.
- Keep GitHub Issues enabled for the repository; the notification feed is issue-comment based.
- Keep your git author email set to your GitHub noreply address if you want automatic handle discovery from commit history.
- If your commits use a non-GitHub email, add your handle to `.github/commit-notify-subscribers.txt`.
