# Redis Protocol Support

VapeCache targets cache-friendly Redis command flows with RESP2/RESP3 parsing support. This document tracks the public command surface exposed via `IRedisCommandExecutor`.

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

### Streams (Redis 8.6 idempotent producer surface)
- `XADD ... IDMP/IDMPAUTO` → `XAddIdempotentAsync`
- `XCFGSET` (idempotence retention knobs) → `XCfgSetIdempotenceAsync`

### Hotkeys (Redis 8.6 diagnostics)
- `HOTKEYS START` → `HotKeysStartAsync`
- `HOTKEYS STOP` → `HotKeysStopAsync`
- `HOTKEYS GET` → `HotKeysGetAsync`

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

## RESP Reply Types

VapeCache parses RESP2 core reply types:
- Simple strings
- Errors
- Integers
- Bulk strings (including null)
- Arrays (including null)

VapeCache also parses RESP3 structural types used in modern deployments:
- Null (`_`)
- Boolean (`#`)
- Double/Big number payload lines
- Verbatim string (`=`)
- Blob error (`!`)
- Set (`~`), Map (`%`), Attribute (`|`), Push (`>`)

## Not Supported (Non-Goals)

- Pub/Sub
- Lua scripting
- Transactions (MULTI/EXEC)
- Full stream consumer-group runtime (`XREAD`/`XREADGROUP`)
- Full cluster orchestration (slot-map ownership across the entire command surface)
- RESP3 client-side caching orchestration

Notes:
- Core cache-path MOVED/ASK redirects are supported with bounded retries.
- RESP3 push frames are safely ignored in request/response loops.
- Enable redirects with `RedisConnection:EnableClusterRedirection=true`.
- Control hop budget with `RedisConnection:MaxClusterRedirects` (default `3`, clamped `0..16`).
