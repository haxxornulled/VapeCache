# Mux PR Review Checklist

Use this checklist for PRs that touch:

- `RedisCommandExecutor`
- `RedisMultiplexedConnection`
- coalesced writes
- ring queues
- autoscaler logic
- runtime normalization or validation
- mux diagnostics or telemetry

For deeper context, read [MUX_MAINTAINER_GUIDE.md](MUX_MAINTAINER_GUIDE.md) first.

## Scope

- Does the PR clearly say whether it changes correctness, latency, throughput, observability, or configuration ergonomics?
- Which lane groups are affected: fast, bulk, pubsub, or blocking?
- Does it touch hot-path routing, autoscaler behavior, queueing, transport reset logic, or option hot reload?

## Correctness

- Does the change preserve lane-local request/response ordering?
- Can you explain why queueing and pending-operation flow still cannot lose, duplicate, double-complete, or over-release work?
- If timeout behavior changed, is RESP framing still safe for transport reuse?
- If transport reset behavior changed, what prevents stale responses, cross-wired completions, or silent sequence mismatch issues?
- If scale-down changed, is drain-before-remove still preserved?

## Lane Model

- Does the change preserve the intended split between fast, bulk, pubsub, and blocking lanes?
- If lane selection scoring changed, did the PR treat it as a tail-latency change rather than a simple balancing tweak?
- Are bulk lanes still excluded from fast-lane autoscaler pressure signals unless that change is explicit and justified?

## Autoscaler

- Does the PR explain its effect on scale-up, scale-down, cooldowns, windows, freeze behavior, and advisor mode?
- Is scaling still bounded and stepwise, or is there a strong reason to change that?
- If the emergency path changed, is it still bounded by max connections and cooldown rules?
- If freeze logic changed, what now replaces reconnect-storm, rate-limit, or flap protection?
- Are min/max connection bounds still enforced?

## Configuration

- Did review cover the runtime normalizer, validator, and transport profile interactions?
- Is it clear whether `TransportProfile` overrides the knob being changed?
- Is `TransportProfile=Custom` required for the new behavior to actually stick?
- Is it explicit whether hot reload affects future lanes only, recycled lanes, or live lanes immediately?
- Are new or changed options documented and validated?

## Coalesced Writes

- Are only valid populated segments handed to socket send?
- Is partial-send accounting still correct?
- Is request-to-wire-length tracking still trustworthy?
- Is buffer ownership returned exactly once?
- Does direct-send fallback still work when coalescing does not apply?
- Does the PR state the intended throughput versus tail-latency tradeoff?

## Observability

- Do `IRedisMultiplexerDiagnostics` and autoscaler snapshot outputs still describe the real runtime state?
- If metrics changed, are per-lane signals still internally consistent?
- If the PR claims throughput gains, did review also check timeout rate, transport resets, orphaned responses, sequence mismatches, and reconnect failures?

## Tests

- Were deterministic tests updated before relying on benchmark evidence?
- For risky changes, were the relevant autoscaler, ring queue, coalesced write, runtime guardrail, and telemetry tests reviewed or extended?
- Does the test coverage prove the invariant that the PR is changing?

## Benchmark And Rollout

- Is correctness evidence separated from benchmark evidence?
- For execution-model changes like `EnableSocketRespReader` or `UseDedicatedLaneWorkers`, is rollout guidance cautious and explicit?
- Could increased batching or concurrency be masking queue pressure instead of fixing the underlying issue?

## Final Review Gate

- Can the reviewer explain the invariant this PR preserves or changes?
- Can the reviewer explain the operator-visible behavior that may change?
- Can the reviewer name the test or metric that would catch a regression here?
