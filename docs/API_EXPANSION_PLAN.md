# API Expansion Backlog

This backlog tracks **future** Redis commands and features that are not yet implemented. It reflects the current codebase and avoids non-goals like Lua scripting.

## Current Coverage

See [REDIS_PROTOCOL_SUPPORT.md](REDIS_PROTOCOL_SUPPORT.md) for the full supported surface.

## Backlog (Candidate Additions)

### Strings
- `INCR`, `DECR`, `INCRBY`, `DECRBY`
- `GETDEL`, `GETSET`
- `APPEND`, `STRLEN`

### Lists
- `LINDEX`, `LTRIM`

### Sets
- `SPOP`, `SRANDMEMBER`
- `SINTER`, `SUNION`, `SDIFF` (and store variants)

### Hashes
- `HDEL`, `HEXISTS`, `HLEN`

### Keys
- `EXISTS`
- `EXPIRE` (public interface)

### Server/Info
- `INFO`, `DBSIZE`

## Non-Goals

These are intentionally out of scope:
- Lua scripting
- Cluster redirects (MOVED/ASK)
- RESP3 push messages

## Notes

When adding commands:
- Update `IRedisCommandExecutor` and `InMemoryCommandExecutor` together.
- Add tests in `VapeCache.Tests`.
- Update [REDIS_PROTOCOL_SUPPORT.md](REDIS_PROTOCOL_SUPPORT.md).
