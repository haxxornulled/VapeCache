# Benchmarking Status

Current benchmark posture is production-grade and actively used for regression detection.

## What Exists

- Head-to-head suites in `VapeCache.Benchmarks` for:
  - Client ops (`RedisClientHeadToHeadBenchmarks`)
  - End-to-end ops (`RedisEndToEndHeadToHeadBenchmarks`)
  - Module ops (`RedisModuleHeadToHeadBenchmarks`)
- Enterprise config in `VapeCache.Benchmarks/EnterpriseBenchmarkConfig.cs`
- Automation scripts:
  - `tools/run-head-to-head-benchmarks.ps1`
  - `tools/run-head-to-head-with-capture.ps1`
  - `tools/run-grocery-head-to-head.ps1`

## How We Judge Wins

A performance claim is accepted only when:

1. Same host + same Redis target + same run mode.
2. At least 3 repeated runs.
3. Median Vape/SER ratio is favorable.
4. Allocation and GC behavior are not regressing.
5. Tail latency checks (p95/p99/p999) are acceptable under sustained load.

## Current Focus Areas

- Bigger payload scaling (1KB/4KB/16KB).
- Allocation pressure reduction on hot paths.
- Sustained load stability and tail latency.
- Reproducible evidence packs for release notes.

## Required Reading

Before modifying benchmark methodology, read:

- `docs/BENCHMARKING.md`
- BenchmarkDotNet official docs listed there
- `docs/ENGINEERING_PLAYBOOK.md`

## Known Gaps

- Some documentation examples historically emphasized means more than tail metrics.
- CI gating still needs stronger percentile/long-run enforcement in all pipelines.

These are documentation/process gaps, not missing benchmark infrastructure.

## Session Checkpoint (2026-03-02)

The current working tree includes uncommitted benchmark and transport work that should be treated as the active checkpoint for the next session.

### Local-Only Changes In Progress

- Added `VapeCache.Benchmarks/Benchmarks/RedisDatatypeParityHeadToHeadBenchmarks.cs`.
- Updated `VapeCache.Benchmarks/BenchmarkRedisConfig.cs` to allow tuned mux overrides from environment variables:
  - `VAPECACHE_BENCH_COALESCED_WRITES`
  - `VAPECACHE_BENCH_DEDICATED_LANE_WORKERS`
  - `VAPECACHE_BENCH_SOCKET_RESP_READER`
- Updated `VapeCache.Benchmarks/BenchmarkSuiteCatalog.cs` to add the `compare datatypes` suite.
- Updated `VapeCache.Tests/Benchmarks/BenchmarkSuiteCatalogTests.cs` for the new suite defaults.
- Updated `tools/run-head-to-head-benchmarks.ps1` to include the `datatypes` suite and force the tuned mux path in head-to-head runs.
- Optimized `VapeCache.Infrastructure/Connections/CoalescedWriteDispatcher.cs` to replace the `List<ArraySegment<byte>>` plus `RemoveAt(0)` partial-send loop with a pooled array plus head-index window.

### Validation Already Completed

- `dotnet build .\VapeCache.Benchmarks\VapeCache.Benchmarks.csproj -c Release --no-restore`
- `dotnet test .\VapeCache.Tests\VapeCache.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~BenchmarkSuiteCatalogTests|FullyQualifiedName~CoalescedWriteDispatcherTests"`
- `dotnet run -c Release --project .\VapeCache.Benchmarks\VapeCache.Benchmarks.Runner.csproj --no-build -- list-suites compare`

These passed and confirmed the `datatypes` suite is discoverable.

### Important Follow-Up

- The first `compare datatypes --job Dry` run initially failed because BenchmarkDotNet does not allow a `sealed` benchmark type. That has already been corrected by changing `RedisDatatypeParityHeadToHeadBenchmarks` to a non-sealed class.
- A later dry run attempt timed out after roughly 244 seconds. That should be rerun at the start of the next session before committing, then the full 3-trial datatype parity run can be executed.
- The matching public-repo sync is only partially done. The shared transport and runner changes were mirrored into `VapeCache-oss`, but the newer suite-catalog abstraction does not exist there in the same form, so any follow-up should keep the two repos aligned intentionally rather than assuming identical benchmark infrastructure.

### Next Recommended Steps

1. Rerun `dotnet run -c Release --project .\VapeCache.Benchmarks\VapeCache.Benchmarks.Runner.csproj --no-build -- compare datatypes --job Dry`.
2. If that passes, run a real `compare datatypes` benchmark against the target Redis instance.
3. Review the datatype parity results before further transport tuning.
4. Commit the current enterprise repo changes only after the dry run succeeds.
