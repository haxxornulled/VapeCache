# Distributed Cache Bridge

`VapeCache.Extensions.DistributedCache` is the interoperability package for teams that need `IDistributedCache` or `IBufferDistributedCache`.

Short version:

- Native VapeCache is the recommended integration.
- The distributed-cache adapter is the migration bridge.
- FusionCache-over-VapeCache is a supported compatibility scenario, not the product headline.
- The production VapeCache runtime behind this bridge does not depend on `StackExchange.Redis`.

## Why This Package Exists

Many .NET applications are already built around the Microsoft distributed-cache abstraction.
That includes middleware, framework features, and cache stacks that are expensive to rewrite in one move.

This package lets those teams route distributed-cache traffic through VapeCache with minimal application churn.

Typical use cases:

- ASP.NET Core components that already require `IDistributedCache`
- incremental migrations away from another distributed cache provider
- evaluation runs where a team wants to keep its current cache abstraction first
- FusionCache deployments that already use the application's distributed-cache registration as L2

## Compatibility Contract

The bridge exists to let teams route distributed-cache traffic through VapeCache without rewriting or destabilizing an existing codebase.

What this package guarantees:

- the public `IDistributedCache` and `IBufferDistributedCache` API surface is implemented
- absolute expiration, relative absolute expiration, and sliding expiration behave correctly from the caller's perspective
- framework and middleware integrations that depend on the Microsoft distributed-cache abstraction can treat VapeCache as the backing provider
- migration and interoperability scenarios are a first-class goal

What this package does not guarantee:

- byte-for-byte backend storage parity with another `IDistributedCache` implementation
- provider-specific internal metadata behavior beyond the public contract
- exposure of native VapeCache runtime semantics through the generic abstraction
- indistinguishable provider identity in every edge case

That is the right contract for this package.
It is strong enough to be useful and safe, without pretending the Microsoft abstraction can represent the full VapeCache runtime model.

## Positioning

Recommended message:

Already on FusionCache or `IDistributedCache`? Route your distributed cache layer through VapeCache with minimal code changes.

That message is more complete when paired with the native-runtime distinction:

- the bridge lets teams keep their current abstraction
- the backing runtime is still VapeCache's own Redis transport and hybrid failover model
- moving to native `IVapeCache` APIs later unlocks the fuller runtime surface

Avoid positioning the adapter as the fullest or best way to use VapeCache.
It is strategically useful for adoption, but it deliberately compresses the runtime behind a generic contract.

## What Maps Cleanly

- `Get` / `GetAsync`
- `Set` / `SetAsync`
- `Remove` / `RemoveAsync`
- `Refresh` / `RefreshAsync`
- absolute expiration relative to now mapped to VapeCache TTL
- `IBufferDistributedCache` support for newer framework integration points

## What This Does Not Preserve

When consumers stay on `IDistributedCache`, they are not using the full native VapeCache experience.

The generic contract hides or weakens the visibility of:

- typed cache ergonomics
- direct cache-intent modeling
- native output-caching integration
- transport and runtime tuning semantics
- the distinction between first-class runtime behavior and compatibility behavior

That tradeoff is intentional.
The bridge exists to reduce adoption friction, not to replace the native API story.

## Sliding Expiration

Sliding expiration is supported, but it is the awkward part of the abstraction.
VapeCache's native TTL model is simpler than the full Microsoft distributed-cache contract, so the adapter manages the extra semantics.

Implementation strategy:

- the adapter stores provider-managed metadata in its own internal envelope format
- caller payloads round-trip as opaque bytes through the public contract
- `Get` and `Refresh` re-apply TTL while respecting any absolute-expiration cap

This keeps the bridge contract correct without pretending backend storage is provider-neutral.

## Setup

```csharp
using VapeCache.Extensions.DependencyInjection;
using VapeCache.Extensions.DistributedCache;

builder.Services.AddVapeCache(builder.Configuration)
    .UseDistributedCacheAdapter(options =>
    {
        options.KeyPrefix = "interop:";
    });
```

You can also register the adapter directly after the core runtime:

```csharp
builder.Services.AddVapeCacheRedisConnections();
builder.Services.AddVapeCacheCaching();
builder.Services.AddVapeCacheDistributedCache(options =>
{
    options.KeyPrefix = "interop:";
});
```

Use a dedicated prefix when the same backend is shared with native VapeCache keys or another distributed-cache consumer.

## FusionCache Compatibility

The intended story is simple:

- keep FusionCache if that is your current application cache stack
- keep the existing `IDistributedCache`-based L2 path if that is how the host is wired
- swap the distributed-cache backing layer to VapeCache

If a FusionCache deployment already consumes the host application's registered `IDistributedCache`, this package is the bridge.

Recommended guidance for that audience:

- use a dedicated `KeyPrefix` such as `fusion:l2:`
- present this as an interoperability layer, not a VapeCache-native integration
- move to native VapeCache APIs later if the team wants the fuller runtime model

What teams should understand in that topology:

- FusionCache stays in charge of app-facing orchestration
- VapeCache becomes the Redis/distributed runtime underneath
- this is a valid complement story, not a downgrade

## Recommendation

Build the bridge.
Document it clearly.
Use it to lower migration friction.
Do not let it replace the native VapeCache story.
