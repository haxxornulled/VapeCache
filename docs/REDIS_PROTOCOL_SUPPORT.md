# Redis Protocol Support

VapeCache targets **RESP2** and focuses on cache-friendly commands. This document tracks the public command surface exposed via `IRedisCommandExecutor`.

## Supported Commands

### Strings
- `GET` → `GetAsync`
- `GETEX` → `GetExAsync`
- `MGET` → `MGetAsync`
- `SET` → `SetAsync`
- `MSET` → `MSetAsync`

### Hashes
- `HSET` → `HSetAsync`
- `HGET` → `HGetAsync`
- `HMGET` → `HMGetAsync`

### Lists
- `LPUSH` → `LPushAsync`
- `RPUSH` → `RPushAsync`
- `LPOP` → `LPopAsync`
- `RPOP` → `RPopAsync`
- `LRANGE` → `LRangeAsync`
- `LLEN` → `LLenAsync`

### Sets
- `SADD` → `SAddAsync`
- `SREM` → `SRemAsync`
- `SISMEMBER` → `SIsMemberAsync`
- `SMEMBERS` → `SMembersAsync`
- `SCARD` → `SCardAsync`

### Sorted Sets
- `ZADD` → `ZAddAsync`
- `ZREM` → `ZRemAsync`
- `ZCARD` → `ZCardAsync`
- `ZSCORE` → `ZScoreAsync`
- `ZRANK` → `ZRankAsync`
- `ZINCRBY` → `ZIncrByAsync`
- `ZRANGE WITHSCORES` → `ZRangeWithScoresAsync`
- `ZRANGEBYSCORE WITHSCORES` → `ZRangeByScoreWithScoresAsync`

### JSON (RedisJSON)
- `JSON.GET` → `JsonGetAsync`, `JsonGetLeaseAsync`
- `JSON.SET` → `JsonSetAsync`, `JsonSetLeaseAsync`
- `JSON.DEL` → `JsonDelAsync`

### RediSearch
- `FT.CREATE` → `FtCreateAsync`
- `FT.SEARCH` → `FtSearchAsync`

### RedisBloom
- `BF.ADD` → `BfAddAsync`
- `BF.EXISTS` → `BfExistsAsync`

### RedisTimeSeries
- `TS.CREATE` → `TsCreateAsync`
- `TS.ADD` → `TsAddAsync`
- `TS.RANGE` → `TsRangeAsync`

### Scan / Streaming
- `SCAN` → `ScanAsync`
- `SSCAN` → `SScanAsync`
- `HSCAN` → `HScanAsync`
- `ZSCAN` → `ZScanAsync`

### Key/Server
- `DEL` → `DeleteAsync`
- `TTL` → `TtlSecondsAsync`
- `PTTL` → `PTtlMillisecondsAsync`
- `UNLINK` → `UnlinkAsync`
- `PING` → `PingAsync`
- `MODULE LIST` → `ModuleListAsync`

## Client-Side Batching

`IRedisBatch` provides client-side pipelining across the supported commands:

```csharp
await using var batch = executor.CreateBatch();
var get = batch.QueueAsync((exec, ct) => exec.GetAsync("key", ct));
var set = batch.QueueAsync((exec, ct) => exec.SetAsync("k2", payload, null, ct));
await batch.ExecuteAsync(ct);
```

## RESP2 Reply Types

VapeCache parses the RESP2 reply types:
- Simple strings
- Errors
- Integers
- Bulk strings (including null)
- Arrays (including null)

## Not Supported (Non-Goals)

- Pub/Sub
- Lua scripting
- Transactions (MULTI/EXEC)
- Cluster redirects (MOVED/ASK)
- RESP3 push messages

If you need these features, use StackExchange.Redis alongside VapeCache.
