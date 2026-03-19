# Mux Maintainer Guide

This guide is for maintainers working on the Redis transport core:

- `RedisCommandExecutor`
- `RedisMultiplexedConnection`
- coalesced writes
- lane routing
- autoscaler behavior

The goal is simple: **understand the current design before tweaking knobs or changing code paths**.

## Read This First

Before changing mux behavior, review these files in this order:

1. `VapeCache.Infrastructure/Connections/RedisCommandExecutor.cs`
2. `VapeCache.Infrastructure/Connections/RedisMultiplexedConnection.cs`
3. `VapeCache.Infrastructure/Connections/CoalescedWriteDispatcher.cs`
4. `VapeCache.Infrastructure/Connections/RedisRuntimeOptionsNormalizer.cs`
5. `VapeCache.Infrastructure/Connections/RedisMultiplexerOptionsValidator.cs`
6. `VapeCache.Tests/Connections/RedisCommandExecutorAutoscalerTests.cs`
7. `VapeCache.Tests/Connections/RingQueueTests.cs`
8. `docs/MUX_FAST_PATH_ARCHITECTURE.md`
9. `docs/ENTERPRISE_MULTIPLEXER_AUTOSCALER.md`
10. `docs/COALESCED_WRITES.md`

If a proposed change conflicts with those docs or tests, assume the existing behavior is intentional until proven otherwise.

## System Model

There are two core layers:

- `RedisCommandExecutor` is the control plane.
  It owns lane arrays, lane-role budgeting, lane selection, autoscaler state, option hot reload, and diagnostics.
- `RedisMultiplexedConnection` is the per-lane data plane.
  It owns enqueue/dequeue, in-flight caps, writer/reader loops, response ordering, timeouts, and transport resets.

The executor does not operate a single homogeneous pool. It manages four lane groups:

- fast
- bulk
- pubsub
- blocking

Only the **fast** group is autoscaled.
Bulk, pubsub, and blocking lanes are role-isolated and intentionally excluded from fast-lane autoscaler pressure signals.

## Request Flow

The hot path is:

1. `RedisCommandExecutor` chooses a lane.
2. `RedisMultiplexedConnection.ExecuteAsync` acquires an in-flight slot.
3. The request is enqueued into `_writes`.
4. `WriterLoopAsync` dequeues it.
5. The writer uses either:
   - coalesced send path
   - direct send path
6. The operation is assigned a response sequence id and enqueued into `_pending`.
7. `ReaderLoopAsync` reads the next RESP frame.
8. The matching `PendingOperation` is completed.

Important consequence:
ordering is lane-local, not global.
Most correctness assumptions depend on each lane maintaining strict request/response order.

## Lane Selection

Fast routing is intentionally simple and cheap:

- generic path: round-robin
- read/write path: power-of-two choice

The scoring function today is:

`writeQueueDepth + (inFlight >> 4)`

That means:

- queue depth is the dominant hot-path pressure signal
- in-flight contributes, but with lower weight

If you change lane selection scoring, you are changing p99 behavior, not just "distribution."

## Lane Roles

Think of the lane groups this way:

- fast lanes: default request/response traffic, shared read/write, autoscaled
- bulk lanes: isolate large payload and pooled-bulk workloads from fast-lane tails
- pubsub lanes: isolate pub/sub work from request/response traffic
- blocking lanes: isolate blocking semantics from request/response traffic

If a change mixes these responsibilities, assume it needs stronger review.

## Autoscaler Mental Model

The autoscaler is a bounded control loop, not a reactive spike detector.

It samples:

- average inflight utilization
- average queue depth
- max queue depth
- timeout rate/sec
- rolling p95 latency
- rolling p99 latency
- unhealthy lane count
- reconnect failure rate/sec

Normal scale-up requires:

- not frozen
- under max connections
- scale-up cooldown elapsed
- sustained high pressure over `ScaleUpWindow`
- confidence from at least 2 high signals

Emergency scale-up is a separate bounded path triggered by timeout spikes.

Scale-down requires:

- not frozen
- above min connections
- scale-down cooldown elapsed
- sustained low pressure over `ScaleDownWindow`
- no unhealthy lanes
- drain-before-remove

The system scales by `+1` or `-1` only.
That is deliberate.

## Guardrails You Should Not Casually Defeat

These guardrails exist because this subsystem has already learned the expensive lessons:

- runtime normalization
- startup validation
- advisor mode
- scale-rate freeze
- flap freeze
- reconnect-storm freeze
- drain-before-remove on scale-down
- transport reset on timeout/framing risk

If you are about to remove one of those protections, stop and write down:

1. what failure mode it currently prevents
2. what new invariant replaces it
3. what deterministic test proves the new behavior is safe

## Timeout and Transport Reset Rule

Current design assumption:

`A timeout can leave RESP framing unsafe for reuse.`

That is why timeout handling can force a transport reset.

Do not optimize this away just to reduce reconnect churn unless you can prove:

- framing remains valid after the timeout mode in question
- pending operations cannot receive cross-wired responses
- sequence mismatch telemetry stays trustworthy

This is a correctness boundary, not just a performance choice.

## Coalesced Writes

Coalescing is a throughput optimization with real complexity.

Treat these as fragile invariants:

- a single Redis command must never be split incorrectly across batches
- only valid populated segments can be handed to vectored socket send
- request buffer ownership must be returned exactly once
- pending operations must be committed only after enough bytes were actually sent

Before changing coalescing logic, re-read:

- `CoalescedWriteDispatcher.cs`
- `CoalescedWriteBatch.cs`
- `docs/COALESCED_WRITES.md`

Especially watch for:

- partial send accounting
- scratch-buffer region reuse
- pooled segment-array lifecycle
- request-to-wire-length mapping

## Ring Queue Rules

The ring queues are hot-path infrastructure.

Do not casually replace them with different primitives unless you are intentionally trading performance for simplicity and have benchmarked it.

The important properties locked in by tests are:

- no loss under contention
- no over-release on canceled enqueue
- bounded capacity
- deterministic drain behavior during dispose

Read `RingQueueTests.cs` before touching queue internals.

For day-to-day reviews, use [MUX_PR_REVIEW_CHECKLIST.md](MUX_PR_REVIEW_CHECKLIST.md).

## Hot Reload Rules

`IOptionsMonitor` updates apply through a runtime snapshot.

Safe expectation:

- new lanes use the latest normalized settings
- existing long-lived lanes keep their current runtime state until recycled

If you change hot-reload semantics, be explicit about whether the change affects:

- only future lanes
- all lanes after recycle
- live lanes immediately

"Immediate live mutation" is the highest-risk path.

## Knobs That Actually Matter

These are the first-order knobs in production.

### Throughput / Tail Tradeoff

- `TransportProfile`
- `Connections`
- `MaxInFlightPerConnection`
- `EnableCoalescedSocketWrites`
- `EnableAdaptiveCoalescing`
- `CoalescedWriteMaxBytes`
- `CoalescedWriteMaxSegments`
- `CoalescingEnterQueueDepth`
- `CoalescingExitQueueDepth`

### Isolation

- `BulkLaneConnections`
- `AutoAdjustBulkLanes`
- `BulkLaneTargetRatio`
- `BulkLaneResponseTimeout`
- `PubSubLaneConnections`
- `BlockingLaneConnections`

### Autoscaler Stability

- `MinConnections`
- `MaxConnections`
- `ScaleUpWindow`
- `ScaleDownWindow`
- `ScaleUpCooldown`
- `ScaleDownCooldown`
- `ScaleUpInflightUtilization`
- `ScaleUpQueueDepthThreshold`
- `ScaleUpTimeoutRatePerSecThreshold`
- `ScaleUpP99LatencyMsThreshold`
- `ScaleDownP95LatencyMsThreshold`
- `MaxScaleEventsPerMinute`
- `FlapToggleThreshold`
- `AutoscaleFreezeDuration`
- `ReconnectStormFailureRatePerSecThreshold`

### Experimental / Validate Carefully

- `EnableSocketRespReader`
- `UseDedicatedLaneWorkers`

These can absolutely be useful, but they change execution characteristics enough that they deserve rollout discipline.

## Safe Change Checklist

Before changing mux/autoscaler behavior:

1. Identify whether the change is about correctness, latency, throughput, observability, or ergonomics.
2. Identify whether it touches fast lanes only, or also bulk/pubsub/blocking lanes.
3. Check whether a transport profile would override the setting anyway.
4. Check whether runtime normalizer or validator already constrains the behavior.
5. Check existing tests for explicit intent before assuming a bug.
6. Add or update deterministic tests before relying on benchmark-only evidence.
7. If the change affects queueing, ordering, or resets, treat it as a correctness-sensitive change.

## What To Benchmark Before Tuning

Use benchmarks only after correctness is locked.

When comparing mux changes, watch:

- throughput
- p95 / p99 / p999 latency
- queue depth stability
- timeout rate
- transport reset count
- orphaned responses
- response sequence mismatches
- reconnect failure rate

If throughput goes up while resets, mismatches, or orphaned responses climb, assume the change is unsafe until proven otherwise.

## What Not To "Improve" Blindly

Do not blindly:

- increase `MaxInFlightPerConnection` to hide queue pressure
- shorten scale-down windows to make the autoscaler look more responsive
- remove freezes because they "block useful scaling"
- merge isolated lane groups back into the fast pool
- turn on socket RESP reader or dedicated workers everywhere by default
- bypass normalization/validation because "operators should know what they are doing"

Most of those changes trade short-term wins for long-term instability.

## Tests To Trust First

If you need a fast signal on intent, start with:

- `RedisCommandExecutorAutoscalerTests`
- `RingQueueTests`
- coalesced write tests
- telemetry tests
- runtime guardrail tests

Those are your best map of what the repo already considers non-negotiable behavior.

## Practical Rule

If you have not looked at:

- current docs
- current tests
- current normalizer
- current validator

then you are not ready to tweak mux behavior yet.

That discipline matters here because this subsystem is part transport, part scheduler, and part control loop.
Small-looking changes can move correctness, tails, and stability all at once.
