# VapeCache.Extensions.DistributedCache

Bridge package for using VapeCache through `IDistributedCache` and `IBufferDistributedCache`.

## Positioning

This package is the migration and interoperability path, not the recommended primary experience.

- Native VapeCache API: recommended
- `IDistributedCache` adapter: compatibility bridge
- FusionCache L2 over VapeCache: supported interop scenario
- Runtime behind the bridge: VapeCache native transport/hybrid runtime (not `StackExchange.Redis` runtime coupling)

For the fuller rationale and positioning, see https://github.com/haxxornulled/VapeCache/blob/main/docs/DISTRIBUTED_CACHE_BRIDGE.md.

## Install

```bash
dotnet add package VapeCache.Extensions.DistributedCache
```

## Setup

```csharp
using Microsoft.Extensions.Caching.Distributed;
using VapeCache.Abstractions.Connections;
using VapeCache.Extensions.DependencyInjection;
using VapeCache.Extensions.DistributedCache;

builder.Services.AddVapeCache(builder.Configuration)
    .UseDistributedCacheAdapter();
```

Optional adapter settings:

```csharp
builder.Services.AddVapeCache(builder.Configuration)
    .UseDistributedCacheAdapter(options =>
    {
        options.KeyPrefix = "fusion:l2:";
    });
```

## Supported Mapping

- `Get` / `GetAsync`
- `Set` / `SetAsync`
- `Remove` / `RemoveAsync`
- `Refresh` / `RefreshAsync`
- absolute expiration to VapeCache TTL
- sliding expiration via adapter-managed metadata envelope

## Compatibility Story

This package is for teams that want to keep an existing `IDistributedCache` surface and swap the distributed backing layer to VapeCache.

That includes:

- ASP.NET Core middleware or framework code that already expects `IDistributedCache`
- migration from another distributed-cache provider
- FusionCache hosts that already use the application's registered distributed-cache service as L2

## Compatibility Contract

This bridge is designed to let teams adopt VapeCache without destabilizing an existing `IDistributedCache`-based codebase.

Guaranteed:

- the full public `IDistributedCache` and `IBufferDistributedCache` method surface is implemented
- caller-visible expiration behavior is preserved for absolute expiration, relative absolute expiration, and sliding expiration
- ASP.NET Core and other framework consumers that depend on the Microsoft distributed-cache contract can use the adapter as a migration/interoperability layer
- arbitrary binary payloads remain valid payloads from the caller's point of view

Not guaranteed:

- backend storage-format parity with other `IDistributedCache` providers
- provider-specific metadata quirks outside the public contract
- access to native VapeCache features that the Microsoft abstraction does not expose
- a claim that the adapter is the fullest representation of the VapeCache runtime

That line is intentional: this package is the bridge that helps teams move to VapeCache without destabilizing the codebase, not the primary way to use the full native runtime model.

## Caveats

- The adapter intentionally flattens VapeCache behind the generic `IDistributedCache` contract.
- Native VapeCache capabilities such as typed APIs, cache intent ergonomics, output-cache-specific integration, and transport/runtime tuning are not fully visible through this abstraction.
- Use a `KeyPrefix` when you want the adapter to coexist cleanly with native VapeCache keys in the same backend.
- For FusionCache-style L2 usage, the recommended framing is "route your distributed cache layer through VapeCache with minimal code changes," not "the adapter is the primary VapeCache experience."

## Docs

- Distributed cache bridge: https://github.com/haxxornulled/VapeCache/blob/main/docs/DISTRIBUTED_CACHE_BRIDGE.md
- Quick start: https://github.com/haxxornulled/VapeCache/blob/main/docs/QUICKSTART.md
- Configuration: https://github.com/haxxornulled/VapeCache/blob/main/docs/CONFIGURATION.md
