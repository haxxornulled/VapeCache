# VapeCache.Extensions.PubSub

Optional Redis pub/sub package for VapeCache.

This package adds explicit registration for `IRedisPubSubService` with bounded delivery queues and reconnect/resubscribe behavior.

## Install

```bash
dotnet add package VapeCache.Extensions.PubSub
```

## Setup

```csharp
using VapeCache.Extensions.DependencyInjection;
using VapeCache.Extensions.PubSub;

builder.Services.AddVapeCache(builder.Configuration)
    .UseRedisPubSub(builder.Configuration);
```

If you are not using the DI facade, you can also register the package directly with `AddVapeCachePubSub(builder.Configuration)`.

## Docs

- Configuration: https://github.com/haxxornulled/VapeCache/blob/main/docs/CONFIGURATION.md#redis-pubsub-optional
- API reference: https://github.com/haxxornulled/VapeCache/blob/main/docs/API_REFERENCE.md
- Quick start: https://github.com/haxxornulled/VapeCache/blob/main/docs/QUICKSTART.md
