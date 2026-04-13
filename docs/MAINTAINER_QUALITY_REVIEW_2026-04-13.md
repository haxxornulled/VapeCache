# Maintainer Quality Review 2026-04-13

This document records the April 13, 2026 hardening pass as a maintainer-facing quality review.

Treat this as accountability documentation, not a vague "bug list." Each item below identifies the regression or maintenance gap, the root cause category, the fix applied, and the prevention rule contributors should follow going forward.

## Why This Review Exists

Three kinds of repository drift had started to appear at the same time:

- runtime ownership logic changed in a correctness-sensitive hot path
- benchmark/docs behavior drifted away from the actual harness
- removed OSS surfaces still existed indirectly through stale docs or excluded tests

That combination is exactly how future blame gets created:

- operators cannot tell which behavior is intentional
- reviewers cannot tell which tests still matter
- maintainers inherit silent regressions because the repository tells two different stories

The goal of this review was to make the repository honest again.

## Issue 1: Buffer Ownership Regression In The Mux Buffer Cache

- Area: `VapeCache.Infrastructure/Connections/RedisMultiplexedBufferCaches.cs`
- Severity: high
- Root cause class: lifecycle regression in a correctness-sensitive pooling path

### What went wrong

The ownership table was being marked as `OwnershipInFlight` during `RentHeaderBuffer` and `RentPayloadArray`.

That sounds small, but it changed the lifecycle contract:

1. A mux-owned buffer was released back to cache.
2. The next renter received the same buffer.
3. When that renter returned it, the caller path still saw `OwnershipInFlight`.
4. The return was ignored instead of caching the buffer again.

The result was silent pool degradation and extra allocations under sustained load.

### Why the bug escaped

- Existing tests only proved "first re-rent works."
- They did not prove "second lifecycle return still reuses the same object."
- The regression lived in a hot-path invariant, not in a compile-time API break.

### Fix applied

- Restored rent-time semantics so renting clears stale ownership markers instead of re-marking the object as in-flight.
- Strengthened `RedisMultiplexedBufferCachesTests` to validate reuse across multiple complete caller/mux/caller lifecycles.

### Prevention rule

If a pooled object can move between caller ownership and mux ownership, reviewers must check all of these transitions explicitly:

1. caller rents
2. caller returns without mux ownership
3. caller hands to mux
4. caller return while mux owns it must be ignored
5. mux returns
6. caller re-rents and returns again

If a test does not cover the full ownership loop, it is not enough.

## Issue 2: Benchmark Harness Default Behavior Drift

- Area: `VapeCache.Benchmarks`
- Severity: medium
- Root cause class: harness behavior/documentation mismatch

### What went wrong

The new BenchmarkDotNet entrypoint ran every benchmark in the assembly by default, including live Redis/KeyDB round-trip benchmarks.

That broke the documented expectation that the default benchmark command is safe on a clean machine without external services.

### Why the bug escaped

- The benchmark harness simplification removed the old launcher behavior.
- Documentation still described the old safety contract.
- No test existed for benchmark default argument shaping.

### Fix applied

- Added `BenchmarkCommandLine` to enforce a safe default:
  - default run => `Micro` category only
  - explicit filter/category selection => respected as-is
  - live opt-in => `--include-live` or `VAPECACHE_BENCH_INCLUDE_LIVE=true`
- Tagged live and micro benchmark classes explicitly.
- Updated benchmark docs and scripts to make live runs opt-in and obvious.
- Added unit tests for default benchmark argument shaping.

### Prevention rule

Any benchmark added to the default harness must declare whether it is:

- safe local microbenchmark
- live external dependency benchmark

If a benchmark requires Redis, KeyDB, Docker, network ACLs, or external services, it must never become part of the default clone-safe path by accident.

## Issue 3: Retired OSS Surfaces Still Influenced Docs And Tests

- Area: docs, tests, old console/demo surfaces
- Severity: medium
- Root cause class: incomplete retirement of removed projects

### What went wrong

The repository intentionally removed the old console host and the legacy benchmark runner, but several traces were left behind:

- stale docs still instructed users to run deleted scripts and projects
- obsolete tests were still present on disk but excluded from compilation

That made the repo look healthier than it was because `dotnet test` passed while silently skipping tests for code that no longer existed.

### Fix applied

- Deleted obsolete benchmark and console-related test files instead of hiding them with `Compile Remove`.
- Replaced benchmark documentation with current harness guidance.
- Replaced retired feature docs with archival notices where the OSS surface no longer exists.
- Updated architecture docs and README text to reflect the current repository surface.

### Prevention rule

When a project is removed from OSS:

1. remove the project from the solution
2. remove its tests, or migrate them to the surviving surface
3. update or archive its docs in the same change
4. do not leave excluded source files behind as silent historical baggage

If a test file no longer represents a shippable surface, delete it or archive it outside the active test project.

## Enterprise Review Rules For Future Contributors

The transport and mux layers should be reviewed as if they were production database drivers, not ordinary helper code.

### Mandatory review questions

1. Does this change affect correctness, or only tuning?
2. Does it change request/response ordering assumptions?
3. Does it change ownership or disposal of pooled buffers?
4. Does it alter timeout/reset behavior after partial I/O?
5. Does it change how diagnostics or autoscaler signals are interpreted?
6. Does it update docs and tests in the same PR?

If the answer to any of 1-5 is yes, the change needs deterministic tests before benchmark claims.

### Changes that deserve extra scrutiny

- response ordering
- pooled buffer lifecycle
- coalesced write partial-send accounting
- timeout handling and transport resets
- lane selection scoring
- autoscaler freeze/cooldown/window logic
- hot reload of mux settings

## Required Validation After Transport/Mux Changes

At minimum:

1. `dotnet build VapeCache.slnx -c Release`
2. `dotnet test VapeCache.Tests/VapeCache.Tests.csproj -c Release`
3. run the benchmark harness default path
4. run at least one explicit live benchmark path if the change touches transport or mux execution
5. update maintainer docs if behavior or invariants changed

## Related Docs

- [TRANSPORT_MUX_AUTOSCALER_DEEP_DIVE.md](TRANSPORT_MUX_AUTOSCALER_DEEP_DIVE.md)
- [MUX_MAINTAINER_GUIDE.md](MUX_MAINTAINER_GUIDE.md)
- [MUX_PR_REVIEW_CHECKLIST.md](MUX_PR_REVIEW_CHECKLIST.md)
- [BENCHMARKING.md](BENCHMARKING.md)
