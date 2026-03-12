# Production Guardrails

This is the operational checklist for shipping VapeCache safely in production.

The four guardrails are:

1. Canary rollout first.
2. Run a Redis failover drill in staging.
3. Watch live cache metrics from minute one.
4. Keep a manual rollback switch ready (forced memory fallback).

## 1) Canary Rollout

Start with partial traffic (for example 5-10%), not full cutover.

Use the guardrail watcher against your canary environment:

```powershell
pwsh -File tools/watch-canary-guardrails.ps1 `
  -AdminBaseUrl "https://admin-canary.example.com" `
  -DurationMinutes 15 `
  -SampleIntervalSeconds 15 `
  -MinHitRate 0.70 `
  -MaxFallbackEventsPerMinute 10
```

The script fails if:

- breaker opens unexpectedly
- hit rate drops below threshold after warmup
- fallback events/minute exceed threshold

## 2) Redis Failover Drill

Run the reconnect drill against a staging endpoint before production rollout:

```powershell
$env:VAPECACHE_REDIS_CONNECTIONSTRING = "redis://staging-redis:6379/0"
pwsh -File tools/run-redis-reconnect-drill.ps1 -Configuration Release
```

This executes `ForcedClientKill_ReconnectsAndSustainsTraffic` and validates reconnect/failover behavior under forced client drops.

## 3) Metrics To Watch

Minimum signals during canary/full rollout:

- `cache.get.hits` / `cache.get.misses`
- `cache.fallback.to_memory`
- `cache.redis.breaker.opened`
- `cache.current.backend`

If wrapper endpoints are enabled, poll:

- `GET /vapecache/status`
- `GET /vapecache/stats`
- `GET /vapecache/stream` (optional realtime SSE)

These should be hosted on an internal wrapper/admin surface, not public edge routes.

## 4) Rollback Switch

Use forced fallback if Redis path becomes unstable.

Enable breaker control endpoints only behind authN/authZ and private network boundaries.

Force memory fallback:

```powershell
pwsh -File tools/invoke-breaker-rollback.ps1 `
  -Action force-open `
  -AdminBaseUrl "https://admin.example.com" `
  -Reason "prod-rollback-2026-03-11"
```

Clear rollback (resume Redis traffic):

```powershell
pwsh -File tools/invoke-breaker-rollback.ps1 `
  -Action clear `
  -AdminBaseUrl "https://admin.example.com"
```

## Fallback Memory Hard Limit

Set a hard memory budget for the in-memory fallback cache:

```json
{
  "InMemorySpill": {
    "MemoryCacheSizeLimitBytes": 536870912
  }
}
```

`MemoryCacheSizeLimitBytes` is in bytes. Set `0` to keep default unbounded behavior.
