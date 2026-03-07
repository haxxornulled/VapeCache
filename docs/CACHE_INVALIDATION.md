# Cache Invalidation Usage Guide

This guide shows how to enable and use policy-driven cache invalidation in VapeCache for production workloads.

## Packages

```bash
dotnet add package VapeCache
dotnet add package VapeCache.Features.Invalidation
```

If you use the application command handlers in this repo, also reference `VapeCache.Application`.

## Register Invalidation

### Autofac

```csharp
using Autofac;
using VapeCache.Features.Invalidation;

var builder = new ContainerBuilder();

builder.AddVapeCacheInvalidation(options =>
{
    options.Enabled = true;
    options.Profile = CacheInvalidationProfile.HighTrafficSite;
});
```

### Microsoft DI

```csharp
using Microsoft.Extensions.DependencyInjection;
using VapeCache.Features.Invalidation;

services.AddVapeCacheInvalidation(options =>
{
    options.Enabled = true;
    options.Profile = CacheInvalidationProfile.SmallWebsite;
});
```

## Choose A Runtime Profile

- `SmallWebsite`: conservative defaults, minimal fan-out.
- `HighTrafficSite`: strict mode with higher concurrency.
- `DesktopApp`: serialized, low-overhead local behavior.

You can still override specific options:

```csharp
options.ThrowOnFailure = false;
options.EvaluatePoliciesInParallel = true;
options.ExecuteTargetsInParallel = true;
options.MaxConcurrency = 32;
```

## Add Policies

### Entity-style policy (recommended)

```csharp
builder.AddHighTrafficEntityInvalidationPolicy<ProductChanged>(
    entityName: "product",
    idsSelector: static e => [e.ProductId],
    zonesSelector: static _ => ["catalog"],
    keyPrefixes: ["product", "product:summary"]);
```

### Direct policies

```csharp
builder.RegisterInstance(new TagInvalidationPolicy<ProductChanged>(
    tagsSelector: static e => [$"product:{e.ProductId}"]));

builder.RegisterInstance(new ZoneInvalidationPolicy<ProductChanged>(
    zonesSelector: static _ => ["catalog"]));

builder.RegisterInstance(new KeyInvalidationPolicy<ProductChanged>(
    keysSelector: static e => [$"product:{e.ProductId}"]));
```

## Dispatch Invalidation

```csharp
var dispatcher = serviceProvider.GetRequiredService<ICacheInvalidationDispatcher>();

var result = await dispatcher.DispatchAsync(new ProductChanged("42"), cancellationToken);

// result includes RequestedTargets/InvalidatedTargets/FailedTargets/SkippedTargets/PolicyFailures
```

## Using Application Command Handlers

If `VapeCache.Application` is referenced, you can use command handlers instead of directly calling policies:

```csharp
using VapeCache.Application.Abstractions;
using VapeCache.Application.Caching.Invalidation.Commands;
using VapeCache.Features.Invalidation;

var handler = serviceProvider.GetRequiredService<
    ICommandHandler<InvalidateEntityCacheCommand, CacheInvalidationExecutionResult>>();

await handler.HandleAsync(new InvalidateEntityCacheCommand(
    EntityName: "product",
    EntityIds: ["42"],
    Zones: ["catalog"],
    KeyPrefixes: ["product", "product:summary"],
    Tags: ["category:electronics"]),
    cancellationToken);
```

## Operational Notes

- Keep `ThrowOnFailure = true` in strict environments where stale data is unacceptable.
- Use zones/tags for broad invalidation and keys for pinpoint removes.
- Keep `MaxConcurrency` bounded to protect thread pool health.
- Validate behavior in integration tests for write paths that must trigger invalidation.

## Security Note

Breaker control endpoints are disabled by default in production. Enable explicitly with:

```json
{
  "VapeCache": {
    "Endpoints": {
      "EnableBreakerControl": true
    }
  }
}
```

Only enable this in trusted environments.