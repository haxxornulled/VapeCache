# Benchmark Claims Policy

This policy defines how VapeCache benchmark claims are written and published.

## Goal

Keep benchmark messaging factual, reproducible, and useful for engineering decisions.  
Avoid marketing-style "winner" claims without context.

## Executor Mode

- `raw`: benchmark mode for head-to-head throughput claims. This bypasses VapeCache hybrid failover and disables the Redis circuit breaker for the VapeCache provider.
- `hybrid`: resiliency drill mode. This uses the normal hybrid executor and circuit breaker so Redis shutdown tests exercise failover instead of surfacing raw transport exceptions.

Do not use `hybrid` mode for authoritative VapeCache-vs-SER throughput claims.

## Report Classes

Use both classes and label them explicitly in every report.

### 1) Strict/Fair (authoritative)

- Purpose: release notes, production readiness, external claims.
- Requirement: same knobs for both providers and both tracks.
- For grocery runs: use `-DisableTrackDefaults`.
- Include repeated runs and median reporting (minimum 3 measured runs).

### 2) Tuned/Showcase (engineering)

- Purpose: demonstrate achievable performance with workload-specific tuning.
- Allows track-aware tuning and profile optimization.
- Must never be presented as universal "always faster" behavior.

## Mandatory Metadata

Every published report must include:

- Date/time (with timezone)
- Commit hash
- Exact command line
- Track (`apples`, `optimized`, `both`)
- Whether track defaults were enabled/disabled
- Redis endpoint class (local/remote) and auth mode (no secret values)
- Host/runtime (`dotnet --info` summary)
- Run counts (warmups, measured, trials)

## Language Rules

Allowed:

- "In this strict/fair run, VapeCache median ratio was X."
- "In tuned mode for this workload, VapeCache achieved Y."
- "Hot-path and parity-path results are reported separately."

Not allowed:

- "VapeCache is always faster than SER."
- "We beat SER" without run class, workload, and date.
- Mixing tuned results into strict/fair claims without labeling.

## Audience Split

- `optimized` track: hot-path capability claims.
- `apples` track: parity/fallback behavior claims.
- `both` track: publish both tables side-by-side.

Do not use only one track to represent both stories.

## Release Claim Gate

A release claim is acceptable only when all are true:

1. Strict/fair report exists and is reproducible.
2. Median ratio is favorable for the specific claim path.
3. Tail latency and allocation regressions are called out.
4. Tuned/showcase numbers (if present) are clearly labeled as tuned.

## Validation Discipline (Required)

Before publishing benchmark claims for a commit, run:

```powershell
dotnet build VapeCache.slnx -c Release
dotnet test VapeCache.Tests/VapeCache.Tests.csproj -c Release
dotnet test VapeCache.PerfGates.Tests/VapeCache.PerfGates.Tests.csproj -c Release
```

If any of these fail, benchmark claims for that snapshot are invalid until fixed.

## Example Commands

Strict/fair grocery report:

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-grocery-head-to-head.ps1 `
  -Trials 5 `
  -Track both `
  -VapeExecutorMode raw `
  -DisableTrackDefaults `
  -ShopperCount 50000 `
  -MaxCartSize 40 `
  -FailBelowRatio 1.0
```

Tuned/showcase grocery report:

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-grocery-head-to-head.ps1 `
  -Trials 5 `
  -Track both `
  -VapeExecutorMode raw `
  -ShopperCount 50000 `
  -MaxCartSize 40 `
  -FailBelowRatio 1.0
```

Failover drill:

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-grocery-head-to-head.ps1 `
  -Trials 1 `
  -Track apples `
  -VapeExecutorMode hybrid `
  -ShopperCount 10000 `
  -MaxCartSize 40
```
