# Transport, Mux, And Autoscaler Deep Dive

This is the maintainer-facing deep dive for the Redis transport stack in VapeCache.

Read this when you need to understand how the runtime actually behaves under pressure, not just what each option is called.

This document complements:

- [MUX_MAINTAINER_GUIDE.md](MUX_MAINTAINER_GUIDE.md)
- [MUX_FAST_PATH_ARCHITECTURE.md](MUX_FAST_PATH_ARCHITECTURE.md)
- [ENTERPRISE_MULTIPLEXER_AUTOSCALER.md](ENTERPRISE_MULTIPLEXER_AUTOSCALER.md)
- [COALESCED_WRITES.md](COALESCED_WRITES.md)

## 1. Layer Model

The transport stack is intentionally split into control plane and data plane responsibilities.

### Control plane

`RedisCommandExecutor`

- owns lane creation and disposal
- normalizes and applies `RedisMultiplexerOptions`
- chooses fast/bulk/pubsub/blocking lanes
- exposes diagnostics snapshots
- owns autoscaler state and decisions

### Data plane

`RedisMultiplexedConnection`

- owns one long-lived connection lane
- enforces in-flight limits
- owns writer and reader loops
- owns request/response ordering inside the lane
- owns transport failure, reset, and completion behavior

### Lower transport primitives

- `RedisConnectionFactory`
- `IRedisConnection`
- RESP readers/writers
- coalesced write dispatcher
- buffer caches and operation pools

The executor decides where work goes.
The lane decides how work survives.

## 2. End-To-End Request Lifecycle

For a normal request/response command:

1. caller creates a command in `RedisCommandExecutor`
2. executor classifies it and chooses a lane
3. lane acquires an in-flight slot
4. request enters the lane write queue
5. writer loop sends bytes using direct or coalesced mode
6. request is assigned a response sequence id and enters the pending-response queue
7. reader loop parses the next RESP frame
8. matching pending operation is completed
9. slot and pooled buffers are returned

Important constraint:

- ordering is lane-local, not pool-global

That means correctness depends on one lane maintaining strict request-to-response order even if other lanes are healthy or faster.

## 3. Lane Roles

The lane pool is not a single anonymous bucket.

### Fast lanes

- default request/response traffic
- main latency-sensitive path
- the only lanes that participate in autoscale decisions

### Bulk lanes

- isolate large payload and pooled bulk operations
- keep fat responses from dominating fast-lane queue depth and tail latency

### Pub/Sub lanes

- isolate subscription workloads from request/response traffic

### Blocking lanes

- isolate blocking Redis semantics from standard traffic

Enterprise mindset:
do not mix these roles casually just because it simplifies wiring. Role isolation is a tail-latency and failure-containment strategy.

## 4. In-Flight And Queueing Model

Every lane enforces `MaxInFlightPerConnection`.

That cap matters for three reasons:

- memory bound
- queue stability
- timeout blast radius

If the in-flight cap is too low, throughput suffers.
If it is too high, timeouts and queue inflation can hide for longer and then fail harder.

The write side uses bounded ring queues:

- MPSC for pending writes
- SPSC for pending responses

This avoids per-operation allocations on the hot path and gives deterministic capacity behavior.

## 5. Coalesced Writes

Coalescing is the main throughput optimization in the mux.

Instead of pushing one socket send per command, the writer can batch multiple pending requests into a single socket write operation.

### Why it exists

- fewer syscalls
- fewer tiny packets
- higher throughput under concurrency

### Why it is dangerous

- partial sends must be accounted for precisely
- sequence assignment must only happen after enough bytes have actually been committed
- pooled header/payload buffers must be returned exactly once
- vectored send inputs must only contain valid populated segments

The correct mental model is:

coalescing is an optimization layer wrapped around correctness-critical wire framing.

If a coalescing change improves throughput but weakens accounting, ownership, or completion ordering, it is not a valid optimization.

## 6. Buffer Ownership Invariants

There are two important pooled objects in the mux send path:

- header buffers
- payload array buffers

They move through three states:

1. caller-owned
2. mux-owned/in-flight
3. returned-to-cache

### Non-negotiable invariant

The same object must never be returned to cache twice, and it must never get stuck permanently marked as in-flight after a successful lifecycle.

That is why there are separate caller-return and mux-return paths.

### Review rule

Any code touching buffer ownership must be validated against this full lifecycle:

- rent
- optional direct caller return
- mark in-flight
- caller return while mux owns
- mux return
- re-rent
- later caller return again

If you only test one pass, you can still miss a pool-degradation bug.

## 7. Response Ordering And Pending Operations

The reader side assumes Redis replies come back in the same order requests were committed on that lane.

That assumption is what allows:

- sequence-based pending completion
- bounded pending rings
- low-allocation hot path

Corollary:

- timeout/reset logic is not just error handling
- it is part of response-order correctness

If framing becomes suspect, the lane must prefer reset over optimistic reuse.

## 8. Timeout And Reset Philosophy

VapeCache treats some timeout modes as a framing-safety risk, not just a slow-operation event.

That is conservative by design.

Why:

- a timed-out request may still have bytes in flight
- the response stream may no longer line up with local pending assumptions
- reusing a suspect connection can cross-wire later responses

The right review question is not "can we avoid reconnect churn?"
It is "can we prove framing is still safe and pending responses cannot be misapplied?"

If the proof is weak, reset wins.

## 9. Transport Profiles

`RedisTransportProfile` exists so operators can choose a bias without hand-tuning every field.

### FullTilt

- throughput-biased
- larger coalesced batches
- wider scatter/gather use

### Balanced

- middle ground
- useful default when operators want less aggressive batching

### LowLatency

- smaller batches
- lower queue-depth thresholds
- favors single-command latency more strongly

### Custom

- use when explicit coalescing values should survive profile application

Important maintainer rule:

If a profile is active and a change claims to tune a coalescing field, verify whether the profile will overwrite that value anyway.

## 10. Autoscaler Model

Autoscaling is a bounded optimization loop, not a correctness dependency.

The fast-lane autoscaler observes:

- average in-flight utilization
- average queue depth
- max queue depth
- timeout rate
- rolling p95 and p99 latency
- lane health
- reconnect failure rate
- spill pressure signals when available

### Normal scale-up

Requires:

- not frozen
- below max connections
- cooldown elapsed
- sustained high pressure across a time window
- confidence from multiple signals

### Emergency scale-up

A bounded faster path driven mainly by timeout spikes.

### Scale-down

Requires:

- not frozen
- above min connections
- sustained low pressure
- no unhealthy lanes
- drain-before-remove

The autoscaler scales by `+1` or `-1`.
That is intentional. Small steps make failures and rollbacks understandable.

## 11. Autoscaler Guardrails

These are not optional niceties. They are the difference between a stable controller and lane thrash.

### Cooldowns

Prevent immediate back-to-back reactions.

### Hysteresis windows

Prevent brief bursts from causing up/down oscillation.

### Freeze rules

Stop the loop when signs point to instability:

- too many scale events per minute
- reconnect storm conditions
- flap toggling

### Drain-before-remove

Protects correctness and in-flight work during scale-down.

Enterprise rule:
never optimize away a guardrail unless you can name the exact replacement invariant and prove it in tests.

## 12. Spill Pressure As A Signal

When spill is enabled, spill metrics can contribute to autoscale confidence.

Why that matters:

- queue depth alone does not always show backpressure early enough
- spill pressure can reveal persistent memory or work skew

Why it is bounded:

- transient spill bursts should not trigger lane churn
- sustained windowing prevents one bad moment from looking like a trend

## 13. What Good Validation Looks Like

### Deterministic tests

Use tests for:

- ownership and disposal rules
- queue correctness
- scale windows and cooldown behavior
- freeze behavior
- timeout/reset decisions

### Benchmarks

Use benchmarks for:

- throughput
- allocation deltas
- tail latency movement
- batch effectiveness

### Live runs

Use live Redis/KeyDB runs for:

- real socket behavior
- transport resets
- reconnect handling
- advisor-mode autoscaler review

Benchmarks do not replace deterministic invariants.
They only measure the behavior you already proved safe.

## 14. Maintainer Checklist Before Approving A Transport Change

1. Which layer is changing: executor, lane, coalescer, reader, or pool?
2. Is the change about correctness or only tuning?
3. Which lane roles are affected?
4. Could this change alter request/response ordering?
5. Could this change alter pooled object ownership?
6. Could this change alter timeout/reset semantics?
7. Is there a deterministic test for the changed invariant?
8. Is there a benchmark or live-run plan if performance is the motivation?
9. Were docs updated where operators or future maintainers will actually look?

If any of 4-7 is unanswered, the change is not ready.
