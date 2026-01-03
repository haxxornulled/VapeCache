# Typed Collection APIs

VapeCache exposes typed wrappers for Redis data structures with automatic serialization.

## Factory

```csharp
public interface ICacheCollectionFactory
{
    ICacheList<T> List<T>(string key);
    ICacheSet<T> Set<T>(string key);
    ICacheHash<T> Hash<T>(string key);
    ICacheSortedSet<T> SortedSet<T>(string key);
}
```

Register via `AddVapecacheCaching()` (or the Autofac module).

## Lists

```csharp
var queue = collections.List<WorkItem>("jobs:pending");
await queue.PushBackAsync(item, ct);
var next = await queue.PopFrontAsync(ct);
```

Fast-fail pop (when the multiplexer is saturated):

```csharp
if (!queue.TryPopFrontAsync(ct, out var task))
{
    return;
}

var item = await task;
```

## Sets

```csharp
var online = collections.Set<string>("users:online");
await online.AddAsync("alice", ct);
var all = await online.MembersAsync(ct);
```

## Hashes

```csharp
var profiles = collections.Hash<UserProfile>("users:profiles");
await profiles.SetAsync("alice", profile, ct);
var loaded = await profiles.GetAsync("alice", ct);
```

## Sorted Sets

```csharp
var leaderboard = collections.SortedSet<string>("scores:weekly");
await leaderboard.AddAsync("alice", 100, ct);
var top = await leaderboard.RangeByRankAsync(0, 9, descending: true, ct);
```

## Serialization

Typed collections use the registered `ICacheCodecProvider`. The default provider is `SystemTextJsonCodecProvider`.

To register a custom codec:

```csharp
services.AddSingleton<ICacheCodecProvider>(sp =>
{
    var provider = new SystemTextJsonCodecProvider();
    provider.Register(new UserDtoCodec());
    return provider;
});
```

## Related Docs
- [API_REFERENCE.md](API_REFERENCE.md)
- [REDIS_PROTOCOL_SUPPORT.md](REDIS_PROTOCOL_SUPPORT.md)
