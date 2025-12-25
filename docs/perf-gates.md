# Perf Gates and Zero-Alloc Checklist

## Hot-path zero-alloc rules
- No `Task`/`TaskCompletionSource` in send/recv/parse hot paths.
- Avoid async state machines; prefer `ValueTask` + `IValueTaskSource`.
- No per-op closures or LINQ; stick to spans/arrays.
- No per-op `CancellationTokenRegistration` unless explicitly enabled.
- No strings in hot path; no `Encoding.GetString`/`ToString`/interpolation.
- No exceptions for control flow; only fatal socket/parse errors.
- No per-op allocating collections.
- Buffers from `ArrayPool`, returned on all paths.
- SAEA awaitables: single continuation registration, correct sync completion, interlocked guard.
- Pending operations pooled; returned to pool on completion.

## PerfGates tests (CI blockers)
Project: `VapeCache.PerfGates.Tests`
- `RespParserLite_ZeroAlloc_CommonFrames` (Tier1, must be 0 B allocated)
- `SocketAwaitableEventArgs_SimulatedCompletion_ZeroAlloc` (Tier1, must be 0 B)
- `AwaitableSocketArgs_SimulatedCompletion_ZeroAlloc` (Tier1, must be 0 B)

Command:
- `dotnet test -c Release VapeCache.PerfGates.Tests/VapeCache.PerfGates.Tests.csproj`

## Standard test suite
- `dotnet test -c Release VapeCache.sln`

## Notes
- Coalesced writes are disabled in comparison benchmarks until SAEA send path is proven stable (`EnableCoalescedSocketWrites=false`).
- For stricter gating, pin the PerfGates tests to Tier1 rules (exact zero) and keep Tier2 (loopback) under small budget.***
