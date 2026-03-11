# Redis Pub/Sub Implementation Notes

Status: phase 1 implemented.

Current state:
- `RedisMultiplexerOptions.PubSubLaneConnections` and `RedisMultiplexerOptions.BlockingLaneConnections` now allocate dedicated mux lane groups.
- `RedisCommandExecutor` keeps pub/sub and blocking lanes isolated from `NextRead`, `NextWrite`, and `NextBulk*` selectors.
- Lane diagnostics now expose `pubsub-*` and `blocking-*` roles.
- Validation enforces lane-budget constraints so at least one fast lane remains.
- `IRedisPubSubService` is implemented with:
  - `PublishAsync(channel, payload)`
  - `SubscribeAsync(channel, handler)` returning `IRedisPubSubSubscription`
  - dedicated subscriber loop with reconnect + resubscribe
  - bounded per-subscription queue with backpressure drop handling
- DI registration now exposes `IRedisPubSubService` from `AddVapecacheCaching()`.

Why:
- Pub/sub connections in Redis are push-oriented and should not share request/response traffic.
- Blocking commands can hold a socket and should not share fast/bulk lanes.

Next steps:
1. Add integration tests with a live Redis instance that validate reconnect + resubscribe behavior end-to-end.
2. Add explicit blocking command APIs (for example `BLPOP`/`BRPOP`) that target blocking lanes only.
3. Add optional pattern subscription support (`PSUBSCRIBE`) if required.
4. Expand telemetry dimensions for pub/sub command classes and delivery outcomes.
