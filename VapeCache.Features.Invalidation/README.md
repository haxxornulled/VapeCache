# VapeCache.Features.Invalidation

Optional policy-driven invalidation package for VapeCache.

## What it adds

- `CacheInvalidationPlan` and `CacheInvalidationPlanBuilder`
- `ICacheInvalidationPolicy<TEvent>` with built-in policy implementations
- `ICacheInvalidationExecutor` to apply plans to `IVapeCache`
- `ICacheInvalidationDispatcher` to run all registered policies for an event
- Runtime configuration via `IOptionsMonitor<CacheInvalidationOptions>`
- Strategic runtime profiles for common deployment shapes

## Runtime Profiles

Set `VapeCache:Invalidation:Profile` to one of:

- `SmallWebsite`: best-effort and low overhead
- `HighTrafficSite`: strict and concurrent execution
- `DesktopApp`: conservative best-effort behavior

You can override profile defaults with:

- `ThrowOnFailure`
- `ExecuteTargetsInParallel`
- `EvaluatePoliciesInParallel`
- `MaxConcurrency`

## Install

```dotnetcli
dotnet add package VapeCache.Features.Invalidation
```

## Example

```csharp
services.AddVapeCacheInvalidation(options =>
{
    options.Enabled = true;
    options.EnableTagInvalidation = true;
    options.Profile = CacheInvalidationProfile.SmallWebsite;
});

services.AddCacheInvalidationPolicy<OrderPlacedEvent, OrderInvalidationPolicy>();
```

```csharp
public sealed class OrderInvalidationPolicy : ICacheInvalidationPolicy<OrderPlacedEvent>
{
    public ValueTask<CacheInvalidationPlan> BuildPlanAsync(OrderPlacedEvent eventData, CancellationToken ct = default)
        => ValueTask.FromResult(new CacheInvalidationPlan(
            tags: [$"order:{eventData.OrderId}", $"customer:{eventData.CustomerId}"],
            zones: ["orders"]));
}
```

```csharp
await dispatcher.DispatchAsync(new OrderPlacedEvent(orderId, customerId), ct);
```

## Out-of-box policies

- `TagInvalidationPolicy<TEvent>`
- `ZoneInvalidationPolicy<TEvent>`
- `KeyInvalidationPolicy<TEvent>`
- `EntityInvalidationPolicy<TEvent>`
- `DelegateInvalidationPolicy<TEvent>`
- `StaticInvalidationPolicy<TEvent>`
- `CompositeInvalidationPolicy<TEvent>`

## One-line policy registration helpers

```csharp
services.AddTagInvalidationPolicy<OrderUpdated>(e => [$"order:{e.OrderId}"]);
services.AddZoneInvalidationPolicy<OrderUpdated>(e => ["orders"]);
services.AddKeyInvalidationPolicy<OrderUpdated>(e => [$"order:summary:{e.OrderId}"]);
services.AddEntityInvalidationPolicy<OrderUpdated>(
    entityName: "order",
    idsSelector: e => [e.OrderId],
    zonesSelector: e => ["orders"],
    keyPrefixes: ["order", "order:summary"]);
```

## Profile-oriented policy shortcuts

```csharp
// Small website: tag + zone invalidation (lower write amplification)
services.AddSmallWebsiteEntityInvalidationPolicy<OrderUpdated>(
    entityName: "order",
    idsSelector: e => [e.OrderId],
    zonesSelector: e => ["orders"]);

// High traffic site: tag + zone + key invalidation (stronger consistency)
services.AddHighTrafficEntityInvalidationPolicy<OrderUpdated>(
    entityName: "order",
    idsSelector: e => [e.OrderId],
    zonesSelector: e => ["orders"]);

// Desktop app: lightweight key-only invalidation
services.AddDesktopKeyInvalidationPolicy<OrderUpdated>(
    keysSelector: e => [$"order:{e.OrderId}"]);
```
