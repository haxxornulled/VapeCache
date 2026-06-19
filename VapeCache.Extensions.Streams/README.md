# VapeCache.Extensions.Streams

Redis Streams extension for VapeCache with Redis 8.6 idempotent producer support.

## Install

```bash
dotnet add package VapeCache.Extensions.Streams
```

## Use This Package When

- you need Redis Streams support as a separate integration package
- you want Redis 8.6 idempotent producer support
- you want the idempotence retention model to stay explicit

## Usage

```csharp
builder.Services.AddVapeCacheStreams(options =>
{
    options.DefaultEntryId = "*";
    options.UseAutoIdempotentId = false;
});
```

## Example

```csharp
var streamProducer = app.Services.GetRequiredService<IRedisStreamIdempotentProducer>();

var entryId = await streamProducer.PublishAsync(
    key: "stream:orders",
    producerId: "orders-api",
    idempotentId: "tx-1001",
    fields:
    [
        ("orderId", (ReadOnlyMemory<byte>)"1001"u8.ToArray()),
        ("status", (ReadOnlyMemory<byte>)"created"u8.ToArray())
    ],
    ct: cancellationToken);
```

## Retention

```csharp
await streamProducer.ConfigureIdempotenceAsync(
    key: "stream:orders",
    durationSeconds: 1800,
    maxSize: 2048,
    ct: cancellationToken);
```

## Docs

- Redis protocol support: https://github.com/haxxornulled/VapeCache/blob/main/docs/REDIS_PROTOCOL_SUPPORT.md
- Package matrix: https://github.com/haxxornulled/VapeCache/blob/main/docs/NUGET_PACKAGES.md
- Source repository: https://github.com/haxxornulled/VapeCache
